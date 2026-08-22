FROM ubuntu:24.04@sha256:33ceb71981b602c1a7443a53469e4dba065f7503eab3078a2d7a57a2ab987517

ARG DEBIAN_FRONTEND=noninteractive
ARG UBUNTU_SNAPSHOT=20260818T000000Z
ARG CA_CERTIFICATES_VERSION=20260601~24.04.1
ARG OPENSSL_VERSION=3.0.13-0ubuntu3.12

RUN apt-get update \
    && apt-get install --yes --no-install-recommends \
        ca-certificates="$CA_CERTIFICATES_VERSION" \
        openssl="$OPENSSL_VERSION" \
    && rm -rf /var/lib/apt/lists/* \
    && apt-get --snapshot "$UBUNTU_SNAPSHOT" update \
    && apt-get --snapshot "$UBUNTU_SNAPSHOT" install --yes --no-install-recommends \
        htop \
        openssh-server \
        socat \
        tmux \
        vim-tiny \
    && rm -rf /var/lib/apt/lists/* /etc/ssh/ssh_host_* \
    && ln -s /usr/bin/vim.tiny /usr/local/bin/vim \
    && useradd --create-home --shell /bin/bash sutty-live \
    && mkdir -p /run/sshd /run/sutty-audit \
    && chown sutty-live:sutty-live /run/sutty-audit

COPY sshd_config /etc/ssh/sshd_config
COPY --chmod=0755 session-audit.sh /usr/local/bin/sutty-session-audit
COPY --chmod=0755 blackhole.sh /usr/local/bin/sutty-blackhole
COPY --chmod=0755 entrypoint.sh /usr/local/bin/sutty-live-entrypoint

EXPOSE 22 2222

ENTRYPOINT ["/usr/local/bin/sutty-live-entrypoint"]
