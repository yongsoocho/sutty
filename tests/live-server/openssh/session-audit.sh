#!/bin/sh
set -eu

audit_file=/run/sutty-audit/events

record_category() {
    printf '%s\n' "$1" >> "$audit_file"
}

case "${SSH_ORIGINAL_COMMAND-}" in
    sutty-lab-audit-summary)
        for category in exec shell sftp other; do
            count=$(awk -v expected="$category" \
                '$0 == expected { count++ } END { print count + 0 }' "$audit_file")
            printf '%s=%s\n' "$category" "$count"
        done
        ;;
    *sftp*)
        record_category sftp
        exec /usr/lib/openssh/sftp-server
        ;;
    "")
        record_category shell
        exec /bin/bash -l
        ;;
    *)
        record_category exec
        exec /bin/bash -c "$SSH_ORIGINAL_COMMAND"
        ;;
esac
