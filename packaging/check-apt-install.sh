#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# != 2 ]]; then
    printf 'Usage: %s CHROOT PACKAGE\n' "$0" >&2
    exit 2
fi

chroot_directory=$(realpath -- "$1")
current_package=$(realpath -- "$2")
packaging_directory=$(cd -- "$(dirname -- "$0")" && pwd)
if [[ "$chroot_directory" = / || ! -x "$chroot_directory/usr/bin/apt-get" ]]; then
    printf 'Expected a disposable Debian or Ubuntu chroot.\n' >&2
    exit 2
fi

working_directory=$(mktemp -d)
trap 'rm -rf -- "$working_directory"' EXIT
export GNUPGHOME="$working_directory/gnupg"
mkdir -m700 -- "$GNUPGHOME"
gpg --batch --pinentry-mode loopback --passphrase '' \
    --quick-generate-key 'SuperSurveyor APT smoke test' rsa2048 sign 1d
APT_SIGNING_FINGERPRINT=$(gpg --batch --with-colons --list-keys \
    | awk -F: '$1 == "fpr" { print $10; exit }')
export APT_SIGNING_FINGERPRINT

current_version=$(dpkg-deb -f "$current_package" Version)
baseline_version="$current_version~smoke1"
baseline_package="$working_directory/supersuveyor_${baseline_version}_amd64.deb"
dpkg-deb --raw-extract "$current_package" "$working_directory/baseline"
sed -i "s/^Version: .*/Version: $baseline_version/" "$working_directory/baseline/DEBIAN/control"
dpkg-deb --root-owner-group --build "$working_directory/baseline" "$baseline_package"

repository_path=/srv/supersuveyor-apt
mkdir -p -- "$chroot_directory$repository_path"
bash "$packaging_directory/build-apt-repository.sh" "$baseline_package" \
    "$chroot_directory$repository_path/baseline" "file://$repository_path/baseline"
bash "$packaging_directory/build-apt-repository.sh" "$current_package" \
    "$chroot_directory$repository_path/current" "file://$repository_path/current"

install -Dm644 "$chroot_directory$repository_path/baseline/supersuveyor.asc" \
    "$chroot_directory/etc/apt/keyrings/supersuveyor.asc"
apt_sources="$chroot_directory/etc/apt/sources.list.d/supersuveyor.sources"
install -Dm644 "$chroot_directory$repository_path/baseline/supersuveyor.sources" "$apt_sources"
chroot "$chroot_directory" apt-get update
chroot "$chroot_directory" apt-get install -y supersuveyor
test "$(chroot "$chroot_directory" dpkg-query -W -f='${Version}' supersuveyor)" = "$baseline_version"
chroot "$chroot_directory" /usr/bin/supersuveyor --help
chroot "$chroot_directory" systemd-analyze verify supersuveyor.service

settings_directory="$chroot_directory/etc/supersuveyor"
settings_path="$settings_directory/settings.json"
install -d -m700 "$settings_directory"
printf '{"smokeTest":true}\n' > "$working_directory/settings.json"
install -m600 "$working_directory/settings.json" "$settings_path"

install -m644 "$chroot_directory$repository_path/current/supersuveyor.sources" "$apt_sources"
chroot "$chroot_directory" apt-get update
chroot "$chroot_directory" apt-get install -y --only-upgrade supersuveyor
test "$(chroot "$chroot_directory" dpkg-query -W -f='${Version}' supersuveyor)" = "$current_version"
cmp "$settings_path" "$working_directory/settings.json"
test "$(stat -c '%a:%u' "$settings_path")" = 600:0
chroot "$chroot_directory" /usr/bin/supersuveyor --help
