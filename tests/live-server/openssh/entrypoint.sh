#!/bin/sh
set -eu

secret_file=/run/secrets/sutty_password
if [ ! -f "$secret_file" ] || [ ! -r "$secret_file" ]; then
    printf '%s\n' 'A readable runtime password secret is required.' >&2
    exit 64
fi

password=$(tr -d '\r\n' < "$secret_file")
if [ "${#password}" -lt 20 ] || [ "${#password}" -gt 128 ] || \
   ! printf '%s' "$password" | grep -Eq '^[A-Za-z0-9]+$'; then
    printf '%s\n' 'The runtime password secret must be 20-128 ASCII alphanumeric characters.' >&2
    exit 64
fi

printf 'sutty-live:%s\n' "$password" | chpasswd
unset password

ssh-keygen -A
install -d -m 0755 /run/sshd
install -d -o sutty-live -g sutty-live -m 0750 /run/sutty-audit
: > /run/sutty-audit/events
chown sutty-live:sutty-live /run/sutty-audit/events
chmod 0640 /run/sutty-audit/events

/usr/sbin/sshd -t -f /etc/ssh/sshd_config
socat TCP-LISTEN:2222,reuseaddr,fork EXEC:/usr/local/bin/sutty-blackhole &

exec /usr/sbin/sshd -D -e -f /etc/ssh/sshd_config
