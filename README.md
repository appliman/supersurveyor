# SuperSurveyor

Get Free Mobile SMS alerts when CPU, memory, or disk usage crosses a threshold. SuperSurveyor runs as a systemd service on Debian 12, Debian 13, and Ubuntu 24.04 LTS (AMD64).

Across five installations sampled after 13–32 minutes of uptime, SuperSurveyor averaged **8.45 MiB of cgroup memory** and **5.8 threads**. The mean per-machine cgroup peak was **9.06 MiB**, and mean process RSS was **15.68 MiB**.

## Install

Copy and paste this block into a Bash terminal. It adds the signed APT repository and installs SuperSurveyor.

If you already have the old `supersuveyor` package installed, run `sudo apt update && sudo apt upgrade` instead. The existing APT source supplies the transition to `supersurveyor` and carries over your settings and service activation. If you installed 0.1.0 from a local file without adding the APT source, use the block below to add it and migrate.

```bash
(
  set -euo pipefail
  sudo apt update
  sudo apt install -y curl gnupg

  repo=https://appliman.github.io/supersurveyor
  key=$(mktemp)
  trap 'rm -f "$key"' EXIT
  curl -fsSL "$repo/supersurveyor.asc" -o "$key"

  fingerprint=$(gpg --batch --with-colons --show-keys "$key" |
    awk -F: '$1 == "pub" { primary = 1 } primary && $1 == "fpr" { print $10; primary = 0 }')
  if [ "$fingerprint" != FD9CCA8302E9924920C64B356A17974BACF6ECB7 ]; then
    echo "Unexpected SuperSurveyor APT key fingerprint." >&2
    exit 1
  fi

  if [ ! -f /etc/apt/sources.list.d/supersuveyor.sources ]; then
    sudo install -Dm644 "$key" /etc/apt/keyrings/supersurveyor.asc
    printf '%s\n' 'Types: deb' "URIs: $repo" 'Suites: stable' \
      'Components: main' 'Architectures: amd64' \
      'Signed-By: /etc/apt/keyrings/supersurveyor.asc' |
      sudo tee /etc/apt/sources.list.d/supersurveyor.sources >/dev/null
  fi

  sudo apt update
  sudo apt install supersurveyor
)
```

## Set up SMS alerts

1. In your [Free Mobile account](https://mobile.free.fr/account/), enable **Mon forfait mobile → Mes options → Notifications par SMS** and find the SMS notification key.
2. Run `sudo supersurveyor` in a terminal. Enter your Free Mobile subscriber ID and SMS notification key. Press Enter to keep the default thresholds.
3. Check your phone for the test SMS. Once Free Mobile accepts the message, SuperSurveyor saves your settings and starts automatically at boot.

If you are already configured or want to re-enter your credentials, run `sudo supersurveyor configure` and answer **y** when asked whether to replace or revalidate them.

<details>
<summary>Downloads, alert defaults, and service commands</summary>

Download the `.deb` from the [latest GitHub release](https://github.com/appliman/supersurveyor/releases/latest) if you prefer a local install. From the download directory, run `sudo apt install ./supersurveyor_0.2.0_amd64.deb`, adjusting the filename to match your download, then follow the SMS setup above.

### Monitoring defaults

SuperSurveyor samples the system every 30 seconds. The default thresholds are configurable.

| Metric | Default alert |
| --- | --- |
| CPU | Above 90% for five continuous minutes |
| Memory | Above 90% on the first reading |
| Disk | Above 85% on the first reading for each filesystem |

Automatic SMS alerts are paused from 22:00 until 07:00 on weekdays or 08:30 on weekends, using the server's local time. The service saves alerts during this period and sends one summary when SMS delivery resumes. To change the schedule, run `sudo supersurveyor configure`.

Disk monitoring includes the root filesystem and automatically discovers local filesystems mounted later. Boot volumes, swap, virtual filesystems, and network mounts are excluded. A volume mounted at several paths is counted once. If the service cannot reach a volume, it records the unavailable measurement in its journal.

SuperSurveyor sends one SMS per threshold breach and re-arms the alert once the reading returns to the threshold or below. Alert state is persisted across restarts. If Free Mobile rejects the credentials, use `sudo supersurveyor configure` to revalidate or replace them.

### Check, update, or remove the service

```bash
sudo systemctl status supersurveyor
sudo journalctl -u supersurveyor -n 50 --no-pager
```

If no test SMS arrives, check that the Free Mobile option is enabled, verify the subscriber ID and key, and check outbound HTTPS access. If setup saved the settings but could not start the service, run `sudo systemctl enable --now supersurveyor` and check the journal.

For systems using the signed repository, upgrade with:

```bash
sudo apt update
sudo apt install --only-upgrade supersurveyor
```

For a local package, install the new `.deb` using the same `sudo apt install ./...` command. Upgrades preserve settings and alert state. `sudo apt remove supersurveyor` stops and removes the service and keeps those files. `sudo apt purge supersurveyor` deletes them.

</details>

<details>
<summary>Build and publish</summary>

### Build from source

Build on Debian 12 AMD64 when targeting all three supported distributions. Install the [.NET 11 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) on the build machine, then run these commands from the repository root:

```bash
sudo apt update
sudo apt install build-essential ca-certificates clang libicu72 libssl-dev zlib1g-dev

dotnet run --project src/SuperSurveyor.Tests/SuperSurveyor.Tests.csproj -c Release
bash packaging/build-deb.sh 0.2.0 ./dist
```

The build produces `dist/supersurveyor_0.2.0_amd64.deb` and the `supersuveyor_0.2.0_all.deb` transition package.

### Publish the signed APT repository

GitHub Pages from a private source repository requires an eligible GitHub plan and organization settings. A public Pages deployment makes the package and its APT metadata publicly downloadable, while the source repository can stay private. [GitHub Pages settings](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site)

1. In the repository, select **Settings → Pages → Build and deployment → Source → GitHub Actions**.
2. Use a dedicated OpenPGP signing key. Set the `APT_SIGNING_KEY` secret to the ASCII-armored private key in the `github-pages` environment. Set the `APT_SIGNING_PASSPHRASE` secret in the same environment if the key is protected. Set the repository variable `APT_SIGNING_FINGERPRINT` to the full primary key fingerprint.
3. Run the **SuperSurveyor APT package** workflow on `main`, enter the new Debian package version, and check **Publish**. The workflow builds the package, tests signed APT installation on the supported distributions, then deploys the signed archive to Pages.
4. Share the deployed HTTPS URL and the expected signing-key fingerprint with the users who will install the package.

You can also publish the static repository on another HTTPS host. With `apt-utils` and `gnupg` installed and the signing key available locally, set `APT_SIGNING_FINGERPRINT` and run the repository builder. If the key is protected, supply `APT_SIGNING_PASSPHRASE` through your secret manager.

```bash
export APT_SIGNING_FINGERPRINT='<full OpenPGP fingerprint>'
bash packaging/build-apt-repository.sh \
  ./apt-site 'https://your-apt-host.example/supersurveyor' ./dist/*.deb
```

Upload the contents of `apt-site` to that HTTPS location. The builder includes signed APT indexes, the package, the public key, and a `supersurveyor.sources` file.

</details>
