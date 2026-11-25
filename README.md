# SuperSurveyor

SuperSurveyor monitors CPU, memory, and mounted local storage on Linux. It sends SMS alerts to the Free Mobile line used for setup. The package and command are named `supersuveyor`.

## Requirements

- Debian 12, Debian 13, or Ubuntu 24.04 LTS on an AMD64 (x86-64) machine running systemd.
- A Free Mobile line and HTTPS access to `smsapi.free-mobile.fr`.
- A user with sudo access. The package contains a .NET 11 Native AOT binary and runs without Docker or a separate .NET runtime.

## Install

### Install from a local Debian package

Download the `supersuveyor-deb` artifact from a successful run of the [APT package workflow](https://github.com/appliman/supersurveyor/actions/workflows/apt.yml) and extract the ZIP. Repository members can run the workflow from **Actions → SuperSurveyor APT package → Run workflow**. Enter a package version and leave **Publish** unchecked to build the artifact only.

From the directory containing the extracted package, run the following commands. Replace the file name if you built a different version.

```bash
sudo apt update
sudo apt install ./supersuveyor_0.1.0_amd64.deb
sudo supersuveyor
```

You can also [build the package on Debian 12](#build-from-source).

### Install from a signed APT repository

Use this method once a maintainer has published the APT repository and provided its HTTPS URL and signing-key fingerprint. If no repository URL has been provided, use the local package above.

Download the archive key and compare its **primary key fingerprint** with the fingerprint provided by the maintainer before installing it.

```bash
sudo apt update
sudo apt install curl gnupg

repo_url='https://your-apt-host.example/supersurveyor'
curl --fail --silent --show-error --location "$repo_url/supersuveyor.asc" -o supersuveyor.asc
gpg --show-keys --fingerprint supersuveyor.asc
```

After verifying the fingerprint, run the following commands in the same shell.

```bash
sudo install -Dm644 supersuveyor.asc /etc/apt/keyrings/supersuveyor.asc

sudo tee /etc/apt/sources.list.d/supersuveyor.sources >/dev/null <<EOF
Types: deb
URIs: $repo_url
Suites: stable
Components: main
Architectures: amd64
Signed-By: /etc/apt/keyrings/supersuveyor.asc
EOF

sudo apt update
sudo apt install supersuveyor
sudo supersuveyor
```

## Configure Free Mobile and start the service

1. Open the [Free Mobile subscriber portal](https://mobile.free.fr/account/) and go to **Mon forfait mobile → Mes options → Notifications par SMS**. Enable the option and copy the SMS notification key. [Free Mobile activation instructions](https://assistance.free.fr/articles/1907)
2. Run `sudo supersuveyor` from an interactive terminal. Enter your Free Mobile subscriber ID and SMS notification key when prompted. The key is hidden as you type. Press Enter at each threshold prompt to accept the displayed default.
3. Check your phone for the test SMS. When the API accepts that message, SuperSurveyor saves the settings, starts the service, and enables it at boot. The settings file is stored at `/etc/supersuveyor/settings.json` with root-only permissions. The service receives it through systemd and runs under a dedicated account.

To change thresholds or replace or revalidate the credentials, run `sudo supersuveyor configure`.

## Monitoring defaults

SuperSurveyor samples the system every 30 seconds. The default thresholds are configurable.

| Metric | Default alert |
| --- | --- |
| CPU | Above 90% for five continuous minutes |
| Memory | Above 90% on the first reading |
| Disk | Above 85% on the first reading for each filesystem |

Disk monitoring includes the root filesystem and automatically discovers local filesystems mounted later. Boot volumes, swap, virtual filesystems, and network mounts are excluded. A volume mounted at several paths is counted once. If the service cannot reach a volume, it records the unavailable measurement in its journal.

SuperSurveyor sends one SMS per threshold breach and re-arms the alert once the reading returns to the threshold or below. Alert state is persisted across restarts. If Free Mobile rejects the credentials, use `sudo supersuveyor configure` to revalidate or replace them.

## Check, update, or remove the service

```bash
sudo systemctl status supersuveyor
sudo journalctl -u supersuveyor -n 50 --no-pager
```

If no test SMS arrives, check that the Free Mobile option is enabled, verify the subscriber ID and key, and check outbound HTTPS access. If setup saved the settings but could not start the service, run `sudo systemctl enable --now supersuveyor` and check the journal.

For systems using the signed repository, upgrade with:

```bash
sudo apt update
sudo apt install --only-upgrade supersuveyor
```

For a local package, install the new `.deb` using the same `sudo apt install ./...` command. Upgrades preserve settings and alert state. `sudo apt remove supersuveyor` keeps those files. `sudo apt purge supersuveyor` deletes them.

## Build from source

Build on Debian 12 AMD64 when targeting all three supported distributions. Install the [.NET 11 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/11.0) on the build machine, then run these commands from the repository root:

```bash
sudo apt update
sudo apt install build-essential ca-certificates clang libicu72 libssl-dev zlib1g-dev

dotnet run --project src/SuperSurveyor.Tests/SuperSurveyor.Tests.csproj -c Release
bash packaging/build-deb.sh 0.1.0 ./dist
```

The result is `dist/supersuveyor_0.1.0_amd64.deb`.

## Publish the signed APT repository

GitHub Pages from a private source repository requires an eligible GitHub plan and organization settings. A public Pages deployment makes the package and its APT metadata publicly downloadable, while the source repository can stay private. [GitHub Pages settings](https://docs.github.com/en/pages/getting-started-with-github-pages/configuring-a-publishing-source-for-your-github-pages-site)

1. In the repository, select **Settings → Pages → Build and deployment → Source → GitHub Actions**.
2. Use a dedicated OpenPGP signing key. Under **Settings → Secrets and variables → Actions**, set the `APT_SIGNING_KEY` secret to the ASCII-armored private key. Set the `APT_SIGNING_PASSPHRASE` secret if the key is protected. Set the `APT_SIGNING_FINGERPRINT` variable to the full primary key fingerprint.
3. Run the **SuperSurveyor APT package** workflow on `main`, enter the new Debian package version, and check **Publish**. The workflow builds the package, tests signed APT installation on the supported distributions, then deploys the signed archive to Pages.
4. Share the deployed HTTPS URL and the expected signing-key fingerprint with the users who will install the package.

You can also publish the static repository on another HTTPS host. With `apt-utils` and `gnupg` installed and the signing key available locally, set `APT_SIGNING_FINGERPRINT` and run the repository builder. If the key is protected, supply `APT_SIGNING_PASSPHRASE` through your secret manager.

```bash
export APT_SIGNING_FINGERPRINT='<full OpenPGP fingerprint>'
bash packaging/build-apt-repository.sh \
  ./dist/supersuveyor_0.1.0_amd64.deb ./apt-site 'https://your-apt-host.example/supersurveyor'
```

Upload the contents of `apt-site` to that HTTPS location. The builder includes signed APT indexes, the package, the public key, and a `supersuveyor.sources` file.
