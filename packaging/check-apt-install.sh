#!/usr/bin/env bash
set -euo pipefail
export LC_ALL=C

if [[ $# != 4 ]]; then
    printf 'Usage: %s CHROOT LEGACY_0.1.0_DEB PACKAGE TRANSITIONAL_PACKAGE\n' "$0" >&2
    exit 2
fi

chroot_directory=$(realpath -- "$1")
legacy_package=$(realpath -- "$2")
packaging_directory=$(cd -- "$(dirname -- "$0")" && pwd)
if [[ "$chroot_directory" = / || ! -x "$chroot_directory/usr/bin/apt-get" ]]; then
    printf 'Expected a disposable Debian or Ubuntu chroot.\n' >&2
    exit 2
fi

printf '%s  %s\n' d681c3f3b4c56a3980ad065c1cb6dded7ff8756ef61ce0b0af1d4a0ad8f06c70 \
    "$legacy_package" | sha256sum --check --status
test "$(dpkg-deb -f "$legacy_package" Package)" = supersuveyor
test "$(dpkg-deb -f "$legacy_package" Version)" = 0.1.0

current_package=
transition_package=
for package in "$3" "$4"; do
    case "$(dpkg-deb -f "$package" Package)" in
        supersurveyor) current_package=$(realpath -- "$package") ;;
        supersuveyor) transition_package=$(realpath -- "$package") ;;
    esac
done
test -n "$current_package"
test -n "$transition_package"
test "$(dpkg-deb -f "$current_package" Architecture)" = amd64
test "$(dpkg-deb -f "$transition_package" Architecture)" = all
current_version=$(dpkg-deb -f "$current_package" Version)
test "$(dpkg-deb -f "$transition_package" Version)" = "$current_version"
dpkg --compare-versions "$current_version" gt 0.1.0

working_directory=$(mktemp -d)
export GNUPGHOME="$working_directory/gnupg"
trap 'gpgconf --kill gpg-agent; rm -rf -- "$working_directory"' EXIT
mkdir -m700 -- "$GNUPGHOME"
gpg --batch --pinentry-mode loopback --passphrase '' \
    --quick-generate-key 'SuperSurveyor APT smoke test' rsa2048 sign 1d
APT_SIGNING_FINGERPRINT=$(gpg --batch --with-colons --list-keys \
    | awk -F: '$1 == "fpr" { print $10; exit }')
export APT_SIGNING_FINGERPRINT

repository_path=/srv/supersurveyor-apt
repository_host="$chroot_directory$repository_path"
feed="$repository_host/feed"
repository_uri="file://$repository_path/feed"
mkdir -p -- "$repository_host"
bash "$packaging_directory/build-apt-repository.sh" "$feed" "$repository_uri" "$legacy_package"

legacy_sources="$chroot_directory/etc/apt/sources.list.d/supersuveyor.sources"
install -Dm644 "$feed/supersuveyor.asc" "$chroot_directory/etc/apt/keyrings/supersuveyor.asc"
install -Dm644 "$feed/supersuveyor.sources" "$legacy_sources"
chroot "$chroot_directory" apt-get update
chroot "$chroot_directory" apt-get install -y supersuveyor
test "$(chroot "$chroot_directory" dpkg-query -W -f='${Version}' supersuveyor)" = 0.1.0
chroot "$chroot_directory" /usr/bin/supersuveyor --help
chroot "$chroot_directory" systemd-analyze verify supersuveyor.service

legacy_uid=$(chroot "$chroot_directory" id -u supersuveyor)
legacy_gid=$(chroot "$chroot_directory" id -g supersuveyor)
legacy_settings="$chroot_directory/etc/supersuveyor"
legacy_state="$chroot_directory/var/lib/supersuveyor"
wants_directory="$chroot_directory/etc/systemd/system/multi-user.target.wants"
install -d -m700 "$legacy_settings"
install -d -m700 -o "$legacy_uid" -g "$legacy_gid" "$legacy_state"
install -d -m755 "$wants_directory"
printf '%s\n' '{"SubscriberId":"simulation","SmsKey":"test-key","CredentialRevision":"3dff5c6e-51be-47e7-aba6-fe2adbab9a62"}' \
    > "$working_directory/settings.json"
printf '%s\n' '{"Episodes":{"cpu":{"Completed":true}}}' > "$working_directory/state.json"
install -m600 "$working_directory/settings.json" "$legacy_settings/settings.json"
install -m600 -o "$legacy_uid" -g "$legacy_gid" "$working_directory/state.json" "$legacy_state/state.json"
ln -s /lib/systemd/system/supersuveyor.service "$wants_directory/supersuveyor.service"

# Replace the repository contents and leave the original client URL and trusted key in place.
bash "$packaging_directory/build-apt-repository.sh" "$repository_host/current" "$repository_uri" \
    "$current_package" "$transition_package"
mv -- "$feed" "$repository_host/previous"
mv -- "$repository_host/current" "$feed"
chroot "$chroot_directory" apt-get update
chroot "$chroot_directory" apt-get upgrade --with-new-pkgs -y
test "$(chroot "$chroot_directory" dpkg-query -W -f='${Version}' supersuveyor)" = "$current_version"
test "$(chroot "$chroot_directory" dpkg-query -W -f='${Version}' supersurveyor)" = "$current_version"
test "$(chroot "$chroot_directory" dpkg-query -W -f='${Architecture}' supersuveyor)" = all
cmp "$legacy_sources" "$feed/supersuveyor.sources"
cmp "$chroot_directory/etc/apt/keyrings/supersuveyor.asc" "$feed/supersuveyor.asc"

settings_path="$chroot_directory/etc/supersurveyor/settings.json"
state_path="$chroot_directory/var/lib/supersurveyor/state.json"
uid=$(chroot "$chroot_directory" id -u supersurveyor)
gid=$(chroot "$chroot_directory" id -g supersurveyor)
cmp "$settings_path" "$working_directory/settings.json"
cmp "$state_path" "$working_directory/state.json"
test ! -e "$legacy_settings/settings.json"
test ! -e "$legacy_state/state.json"
test "$(stat -c '%a:%u:%g' "$settings_path")" = 600:0:0
test "$(stat -c '%a:%u:%g' "$state_path")" = "600:$uid:$gid"
test -L "$wants_directory/supersurveyor.service"
test ! -L "$wants_directory/supersuveyor.service"
chroot "$chroot_directory" /usr/bin/supersurveyor --help
chroot "$chroot_directory" /usr/bin/supersuveyor --help
chroot "$chroot_directory" systemd-analyze verify supersurveyor.service

# A direct install on the canonical source must not pull the transitional package or enable an unconfigured service.
chroot "$chroot_directory" apt-get purge -y supersuveyor supersurveyor
rm -- "$legacy_sources"
install -Dm644 "$feed/supersurveyor.asc" "$chroot_directory/etc/apt/keyrings/supersurveyor.asc"
install -Dm644 "$feed/supersurveyor.sources" "$chroot_directory/etc/apt/sources.list.d/supersurveyor.sources"
chroot "$chroot_directory" apt-get update
chroot "$chroot_directory" apt-get install -y supersurveyor
test "$(chroot "$chroot_directory" dpkg-query -W -f='${Version}' supersurveyor)" = "$current_version"
if chroot "$chroot_directory" dpkg-query -W -f='${db:Status-Status}' supersuveyor 2>/dev/null | grep -qx installed; then
    printf 'A clean install unexpectedly installed the transitional package.\n' >&2
    exit 1
fi
test ! -e "$settings_path"
test ! -e "$state_path"
test ! -L "$wants_directory/supersurveyor.service"
chroot "$chroot_directory" /usr/bin/supersurveyor --help
