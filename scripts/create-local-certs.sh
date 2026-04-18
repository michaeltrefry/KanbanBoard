#!/usr/bin/env bash

set -euo pipefail

if ! command -v mkcert >/dev/null 2>&1; then
  echo "mkcert is required. Install it first, then rerun this script." >&2
  exit 1
fi

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cert_dir="${repo_root}/certs"

mkdir -p "${cert_dir}"

mkcert -install
mkcert \
  -cert-file "${cert_dir}/localhost.pem" \
  -key-file "${cert_dir}/localhost-key.pem" \
  localhost 127.0.0.1 ::1

echo "Generated development certificates in ${cert_dir}"
