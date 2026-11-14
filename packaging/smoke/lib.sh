#!/usr/bin/env bash
# Helpers shared by smoke-linux.sh and smoke-macos.sh.

fail() { printf 'SMOKE FAILED: %s\n' "$*" >&2; exit 1; }
step() { printf '\n== %s\n' "$*"; }

SERVER_PID=""
stop_release_server() { [ -z "$SERVER_PID" ] || kill "$SERVER_PID" 2>/dev/null || true; }

# start_release_server <artifact-dir> <port>
# Serves the artifact directory like a GitHub release would, including a SHA256SUMS.txt for the CLI archives.
start_release_server() {
  local dir="$1" port="$2"
  (
    cd "$dir"
    if command -v sha256sum >/dev/null 2>&1; then sha256sum -- vibe-coding-hub-cli-*; else shasum -a 256 -- vibe-coding-hub-cli-*; fi > SHA256SUMS.txt
  )
  python3 -m http.server "$port" --bind 127.0.0.1 --directory "$dir" >/dev/null 2>&1 &
  SERVER_PID=$!
  trap stop_release_server EXIT
  sleep 1
  kill -0 "$SERVER_PID" 2>/dev/null || fail "could not start the local release server on port $port"
}

# test_install_script <repo-root> <version> <port> <install-dir>
# Runs install.sh against the local server (real archive, real checksum) and runs the installed binary.
test_install_script() {
  local root="$1" version="$2" port="$3" target="$4"
  BASE_URL="http://127.0.0.1:$port" VERSION="$version" INSTALL_DIR="$target" bash "$root/install.sh" || fail "install.sh failed"
  [ -x "$target/aiagent" ] || fail "install.sh did not install $target/aiagent"
  "$target/aiagent" --version | grep -F "$version" || fail "aiagent installed by install.sh does not report version $version"
}
