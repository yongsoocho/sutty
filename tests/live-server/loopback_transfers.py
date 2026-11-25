"""Run real SSH/SFTP through two temporary loopback endpoints on the Windows CI runner."""

import asyncio
import hashlib
import os
from functools import partial
from pathlib import Path
import secrets
import stat
from tempfile import TemporaryDirectory

import asyncssh


PROJECT = Path(__file__).resolve().parents[1] / "sutty.LiveServer.SelfTest" / "sutty.LiveServer.SelfTest.csproj"
LOOPBACK = "127.0.0.1"
MIB = 1024 * 1024
SOURCE_MTIME = 1_612_345_678
ORIGINAL_MTIME = 1_500_000_000


class MetadataServer(asyncssh.SFTPServer):
    def setstat(self, path, attrs):
        if path.endswith(b".part") and attrs.mtime is not None:
            if path.startswith(b"/deny-"):
                raise asyncssh.SFTPPermissionDenied("Metadata update denied")
            if path.startswith(b"/ignore-"):
                return None
        return super().setstat(path, attrs)


class Endpoint(asyncssh.SSHServer):
    def __init__(self, username, password, forward_to=None, forwards=None):
        self.username = username
        self.password = password
        self.forward_to = forward_to
        self.forwards = forwards

    def password_auth_supported(self):
        return True

    def validate_password(self, username, password):
        return username == self.username and secrets.compare_digest(password, self.password)

    def connection_requested(self, dest_host, dest_port, orig_host, orig_port):
        if self.forward_to is not None and (dest_host, dest_port) == (LOOPBACK, self.forward_to):
            self.forwards.append(dest_port)
            return True
        return False


class OperatorTarget(Endpoint):
    def __init__(self, password, client_key, code, echo_port):
        super().__init__("target", password)
        self.client_key = client_key
        self.code = code
        self.echo_port = echo_port

    def public_key_auth_supported(self):
        return True

    def validate_public_key(self, username, key):
        return username == "key-user" and key == self.client_key

    def kbdint_auth_supported(self):
        return True

    def get_kbdint_challenge(self, username, lang, submethods):
        if username != "otp-user":
            return False
        return ("Pilot", "", "", [("Verification code: ", False)])

    def validate_kbdint_response(self, username, responses):
        return username == "otp-user" and len(responses) == 1 and secrets.compare_digest(responses[0], self.code)

    def connection_requested(self, dest_host, dest_port, orig_host, orig_port):
        return (dest_host, dest_port) == (LOOPBACK, self.echo_port)


async def echo_line(reader, writer):
    try:
        writer.write(await asyncio.wait_for(reader.readexactly(4), timeout=15))
        await writer.drain()
    finally:
        writer.close()
        await writer.wait_closed()


def destination_command(process):
    if process.command == "sutty-test-endpoint":
        process.stdout.write("destination\n")
        process.exit(0)
    else:
        process.exit(1)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def prepare_file(path, size):
    # Nonzero data on both sides of the interruption also catches seek/append mistakes.
    with path.open("wb") as stream:
        stream.truncate(size)
        for offset in (0, 4 * MIB - 32768, size // 2, size - 65536):
            stream.seek(offset)
            stream.write(os.urandom(65536))


async def run_case(direction, local, remote_root, size, settings, forwards):
    remote = remote_root / local.name
    source, destination = (local, remote) if direction == "upload" else (remote, local)
    prepare_file(source, size)
    os.utime(source, ns=(SOURCE_MTIME * 10**9, SOURCE_MTIME * 10**9 + 750_000_000))
    destination.write_bytes(b"previous")
    os.utime(destination, (ORIGINAL_MTIME, ORIGINAL_MTIME))
    if direction == "upload" and os.name == "posix":
        destination.chmod(0o751)
    expected = digest(source)
    env = os.environ | {"SUTTY_LOOPBACK_" + key: str(value) for key, value in settings.items()}
    env["SUTTY_LOOPBACK_EXPECTED_HASH"] = expected
    partial = 0
    routes = ("direct", "jump") if direction == "upload" else ("jump", "direct")
    for action, route in zip(("interrupt", "resume"), routes):
        count = len(forwards)
        proc = await asyncio.create_subprocess_exec(
            "dotnet", "run", "--project", str(PROJECT), "--configuration", "Debug", "--no-build",
            "--no-restore", "--", "--loopback-transfer", direction, route, action, str(local),
            str(size), str(partial), env=env,
        )
        if await proc.wait() != 0:
            raise RuntimeError(f"{direction} {action} via {route} failed")
        # The held transfer and its overlapping browse must both use the same gateway as the shell.
        minimum = 3 if action == "interrupt" else 2
        if route == "jump" and len(forwards) - count < minimum:
            raise AssertionError(f"Expected at least {minimum} target connections through the gateway")
        if route == "direct" and len(forwards) != count:
            raise AssertionError("Direct unexpectedly used the gateway")
        staged = list(destination.parent.glob(destination.name + ".sutty-*.part"))
        if action == "interrupt":
            if destination.read_bytes() != b"previous" or int(destination.stat().st_mtime) != ORIGINAL_MTIME or len(staged) != 1:
                raise AssertionError("Interrupt must preserve the original file and its timestamp")
            if direction == "upload" and os.name == "posix" and staged[0].stat().st_mode & 0o006:
                raise AssertionError("The partial granted other users access that the destination denies")
            partial = staged[0].stat().st_size
            if not 0 < partial < size:
                raise AssertionError(f"Invalid staged length {partial}")
        elif staged or destination.stat().st_size != size or digest(destination) != expected:
            raise AssertionError("Published file or cleanup failed independent on-disk verification")
        elif int(destination.stat().st_mtime) != SOURCE_MTIME:
            raise AssertionError("Resumed transfer lost the source modification time")
        if direction == "upload" and os.name == "posix" and stat.S_IMODE(destination.stat().st_mode) != 0o751:
            raise AssertionError("Upload changed executable permissions")


async def main(root):
    remote_root = root / "target-files"
    remote_root.mkdir()
    browse = remote_root / "browse-check"
    browse.mkdir()
    (browse / "확인.txt").write_text("ready", encoding="utf-8")
    keys = [asyncssh.generate_private_key("ssh-ed25519") for _ in range(2)]
    target_secret, gateway_secret = (secrets.token_urlsafe(32) for _ in range(2))
    forwards = []
    client_key = asyncssh.generate_private_key("ssh-ed25519")
    client_key_file = root / "pilot.key"
    client_key.write_private_key(client_key_file)
    code = secrets.token_hex(6)
    for route in ("direct", "jump"):
        for prefix in ("edit", "deny", "ignore"):
            file = remote_root / f"{prefix}-{route}.txt"
            file.write_bytes(b"first")
            os.utime(file, (ORIGINAL_MTIME, ORIGINAL_MTIME))
            if os.name == "posix":
                file.chmod(0o750)
    echo = await asyncio.start_server(echo_line, LOOPBACK, 0)
    echo_port = echo.sockets[0].getsockname()[1]
    async with echo, asyncssh.listen(
        LOOPBACK, 0, server_factory=partial(OperatorTarget, target_secret, client_key.convert_to_public(), code, echo_port),
        server_host_keys=[keys[0]], process_factory=destination_command,
        sftp_factory=lambda channel: MetadataServer(channel, chroot=str(remote_root)),
    ) as target:
        target_port = target.get_port()
        async with asyncssh.listen(
            LOOPBACK, 0, server_factory=lambda: Endpoint("gateway", gateway_secret, target_port, forwards),
            server_host_keys=[keys[1]],
        ) as gateway:
            settings = {
                "TARGET_PORT": target_port, "GATEWAY_PORT": gateway.get_port(),
                "TARGET_PASSWORD": target_secret, "GATEWAY_PASSWORD": gateway_secret,
                "TARGET_KEY": keys[0].get_fingerprint(), "GATEWAY_KEY": keys[1].get_fingerprint(),
            }
            operator_env = settings | {"CLIENT_KEY_FILE": client_key_file, "TARGET_CODE": code, "ECHO_PORT": echo_port}
            env = os.environ | {"SUTTY_LOOPBACK_" + key: str(value) for key, value in operator_env.items()}
            for route in ("direct", "jump"):
                process = await asyncio.create_subprocess_exec(
                    "dotnet", "run", "--project", str(PROJECT), "--configuration", "Debug", "--no-build",
                    "--no-restore", "--", "--loopback-operator", route, str(root), env=env,
                )
                if await process.wait() != 0:
                    raise RuntimeError(f"Operator acceptance failed via {route}")
                if (remote_root / f"edit-{route}.txt").read_bytes() != b"other":
                    raise AssertionError("A conflicting edit changed the remote file")
                for prefix in ("edit", "deny", "ignore"):
                    file = remote_root / f"{prefix}-{route}.txt"
                    if os.name == "posix" and stat.S_IMODE(file.stat().st_mode) != 0o750:
                        raise AssertionError("Remote edit changed the original permissions")
                    if prefix != "edit" and (file.read_bytes() != b"first" or int(file.stat().st_mtime) != ORIGINAL_MTIME):
                        raise AssertionError("Metadata failure changed the original file")
            await run_case("upload", root / "upload.bin", remote_root, 1024 * MIB, settings, forwards)
            await run_case("download", root / "download.bin", remote_root, 16 * MIB, settings, forwards)


if __name__ == "__main__":
    with TemporaryDirectory(prefix="sutty-transfer-") as temporary:
        asyncio.run(main(Path(temporary)))
