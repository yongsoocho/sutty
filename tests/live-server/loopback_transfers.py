"""Run real SSH/SFTP through two temporary loopback endpoints on the Windows CI runner."""

import asyncio
import hashlib
import os
from pathlib import Path
import secrets
from tempfile import TemporaryDirectory

import asyncssh


PROJECT = Path(__file__).resolve().parents[1] / "sutty.LiveServer.SelfTest" / "sutty.LiveServer.SelfTest.csproj"
LOOPBACK = "127.0.0.1"
MIB = 1024 * 1024


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
            if destination.exists() or len(staged) != 1:
                raise AssertionError("Expected one staged file and no final destination")
            partial = staged[0].stat().st_size
            if not 0 < partial < size:
                raise AssertionError(f"Invalid staged length {partial}")
        elif staged or destination.stat().st_size != size or digest(destination) != expected:
            raise AssertionError("Published file or cleanup failed independent on-disk verification")


async def main(root):
    remote_root = root / "target-files"
    remote_root.mkdir()
    browse = remote_root / "browse-check"
    browse.mkdir()
    (browse / "확인.txt").write_text("ready", encoding="utf-8")
    keys = [asyncssh.generate_private_key("ssh-ed25519") for _ in range(2)]
    target_secret, gateway_secret = (secrets.token_urlsafe(32) for _ in range(2))
    forwards = []
    async with asyncssh.listen(
        LOOPBACK, 0, server_factory=lambda: Endpoint("target", target_secret),
        server_host_keys=[keys[0]], process_factory=destination_command,
        sftp_factory=lambda channel: asyncssh.SFTPServer(channel, chroot=str(remote_root)),
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
            await run_case("upload", root / "upload.bin", remote_root, 1024 * MIB, settings, forwards)
            await run_case("download", root / "download.bin", remote_root, 16 * MIB, settings, forwards)


if __name__ == "__main__":
    with TemporaryDirectory(prefix="sutty-transfer-") as temporary:
        asyncio.run(main(Path(temporary)))
