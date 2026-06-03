# syntax=docker/dockerfile:1.4

# DxMessaging Development Container
# Base image: .NET 9.0 on Debian Bookworm
# Note: .NET 10 SDK is installed alongside .NET 9 to support C# Dev Kit.
# Note: docker-outside-of-docker (DooD) is enabled via devcontainer feature so
#       Phase 2+ can spawn ephemeral unityci/editor containers from inside.
FROM mcr.microsoft.com/devcontainers/dotnet:1-9.0-bookworm

# Build argument for target architecture (amd64 or arm64)
ARG TARGETARCH=amd64

# Container labels
LABEL org.opencontainers.image.source="https://github.com/Ambiguous-Interactive/DxMessaging"
LABEL org.opencontainers.image.description="DxMessaging development container"
LABEL org.opencontainers.image.licenses="MIT"
LABEL unity.support="docker-outside-of-docker"
LABEL unity.note="Unity tests spawn ephemeral unityci/editor containers via DooD"

# Environment variables
ENV DEBIAN_FRONTEND=noninteractive
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1
ENV DOTNET_NOLOGO=1

# ------------------------------------------------------------------------------
# Fix expired Yarn GPG key issue
# The base image includes the Yarn repository which has an expired signing key.
# Remove it since this project doesn't require Yarn.
# This must run BEFORE the first apt-get update so feature installation does
# not fail on the expired signature.
# ------------------------------------------------------------------------------
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    rm -f /etc/apt/sources.list.d/yarn.list \
    && rm -f /usr/share/keyrings/yarn-keyring.gpg \
    && rm -f /etc/apt/keyrings/yarn.gpg \
    && rm -f /usr/share/keyrings/yarn.gpg \
    && rm -f /etc/apt/trusted.gpg.d/yarn.gpg \
    && find /etc/apt/sources.list.d/ -name "*yarn*" -delete || true

# Install essential APT packages
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    apt-get update && apt-get install -y --no-install-recommends \
        ripgrep \
        jq \
        htop \
        ncdu \
        tree \
        tldr \
        silversearcher-ag \
        moreutils \
        unzip \
        wget \
        ca-certificates \
        shellcheck \
        curl \
        gnupg \
        apt-transport-https \
        software-properties-common \
    && apt-get autoremove -y

# ------------------------------------------------------------------------------
# Install .NET 10 SDK side-by-side (required by C# Dev Kit extension)
# Unity is targeting .NET Standard 2.1 / .NET 9, so .NET 9 SDK remains primary.
# We install .NET 10 SDK + runtime via the official dotnet-install.sh into a
# shared location so both vscode and root see it on PATH.
# ------------------------------------------------------------------------------
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    --mount=type=tmpfs,target=/tmp \
    apt-get update \
    && apt-get -y install --no-install-recommends \
        wget \
        ca-certificates \
    && wget https://dot.net/v1/dotnet-install.sh -O /tmp/dotnet-install.sh \
    && chmod +x /tmp/dotnet-install.sh \
    && /tmp/dotnet-install.sh --channel 10.0 --install-dir /usr/share/dotnet \
    && apt-get autoremove -y

# Create a temporary directory for downloads
WORKDIR /tmp/tools

# ------------------------------------------------------------------------------
# Install fd (fd-find) 10.2.0
# https://github.com/sharkdp/fd
# ------------------------------------------------------------------------------
RUN set -eux; \
    FD_VERSION="10.2.0"; \
    case "${TARGETARCH}" in \
        amd64) FD_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) FD_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/sharkdp/fd/releases/download/v${FD_VERSION}/fd-v${FD_VERSION}-${FD_ARCH}.tar.gz" -O fd.tar.gz; \
    tar -xzf fd.tar.gz; \
    cp "fd-v${FD_VERSION}-${FD_ARCH}/fd" /usr/local/bin/fd; \
    chmod +x /usr/local/bin/fd; \
    rm -rf fd.tar.gz "fd-v${FD_VERSION}-${FD_ARCH}"; \
    fd --version

# ------------------------------------------------------------------------------
# Install bat 0.24.0
# https://github.com/sharkdp/bat
# ------------------------------------------------------------------------------
RUN set -eux; \
    BAT_VERSION="0.24.0"; \
    case "${TARGETARCH}" in \
        amd64) BAT_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) BAT_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/sharkdp/bat/releases/download/v${BAT_VERSION}/bat-v${BAT_VERSION}-${BAT_ARCH}.tar.gz" -O bat.tar.gz; \
    tar -xzf bat.tar.gz; \
    cp "bat-v${BAT_VERSION}-${BAT_ARCH}/bat" /usr/local/bin/bat; \
    chmod +x /usr/local/bin/bat; \
    rm -rf bat.tar.gz "bat-v${BAT_VERSION}-${BAT_ARCH}"; \
    bat --version

# ------------------------------------------------------------------------------
# Install fzf 0.56.3
# https://github.com/junegunn/fzf
# ------------------------------------------------------------------------------
RUN set -eux; \
    FZF_VERSION="0.56.3"; \
    case "${TARGETARCH}" in \
        amd64) FZF_ARCH="linux_amd64" ;; \
        arm64) FZF_ARCH="linux_arm64" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/junegunn/fzf/releases/download/v${FZF_VERSION}/fzf-${FZF_VERSION}-${FZF_ARCH}.tar.gz" -O fzf.tar.gz; \
    tar -xzf fzf.tar.gz; \
    cp fzf /usr/local/bin/fzf; \
    chmod +x /usr/local/bin/fzf; \
    rm -rf fzf.tar.gz fzf; \
    fzf --version

# ------------------------------------------------------------------------------
# Install delta 0.18.2
# https://github.com/dandavison/delta
# ------------------------------------------------------------------------------
RUN set -eux; \
    DELTA_VERSION="0.18.2"; \
    case "${TARGETARCH}" in \
        amd64) DELTA_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) DELTA_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/dandavison/delta/releases/download/${DELTA_VERSION}/delta-${DELTA_VERSION}-${DELTA_ARCH}.tar.gz" -O delta.tar.gz; \
    tar -xzf delta.tar.gz; \
    cp "delta-${DELTA_VERSION}-${DELTA_ARCH}/delta" /usr/local/bin/delta; \
    chmod +x /usr/local/bin/delta; \
    rm -rf delta.tar.gz "delta-${DELTA_VERSION}-${DELTA_ARCH}"; \
    delta --version

# ------------------------------------------------------------------------------
# Install eza 0.20.14
# https://github.com/eza-community/eza
# ------------------------------------------------------------------------------
RUN set -eux; \
    EZA_VERSION="0.20.14"; \
    case "${TARGETARCH}" in \
        amd64) EZA_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) EZA_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/eza-community/eza/releases/download/v${EZA_VERSION}/eza_${EZA_ARCH}.tar.gz" -O eza.tar.gz; \
    tar -xzf eza.tar.gz; \
    cp eza /usr/local/bin/eza; \
    chmod +x /usr/local/bin/eza; \
    rm -rf eza.tar.gz eza; \
    eza --version

# ------------------------------------------------------------------------------
# Install zoxide 0.9.6
# https://github.com/ajeetdsouza/zoxide
# ------------------------------------------------------------------------------
RUN set -eux; \
    ZOXIDE_VERSION="0.9.6"; \
    case "${TARGETARCH}" in \
        amd64) ZOXIDE_ARCH="x86_64-unknown-linux-musl" ;; \
        arm64) ZOXIDE_ARCH="aarch64-unknown-linux-musl" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/ajeetdsouza/zoxide/releases/download/v${ZOXIDE_VERSION}/zoxide-${ZOXIDE_VERSION}-${ZOXIDE_ARCH}.tar.gz" -O zoxide.tar.gz; \
    tar -xzf zoxide.tar.gz; \
    cp zoxide /usr/local/bin/zoxide; \
    chmod +x /usr/local/bin/zoxide; \
    rm -rf zoxide.tar.gz zoxide; \
    zoxide --version

# ------------------------------------------------------------------------------
# Install yq 4.44.6
# https://github.com/mikefarah/yq
# ------------------------------------------------------------------------------
RUN set -eux; \
    YQ_VERSION="4.44.6"; \
    case "${TARGETARCH}" in \
        amd64) YQ_ARCH="linux_amd64" ;; \
        arm64) YQ_ARCH="linux_arm64" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/mikefarah/yq/releases/download/v${YQ_VERSION}/yq_${YQ_ARCH}" -O /usr/local/bin/yq; \
    chmod +x /usr/local/bin/yq; \
    yq --version

# ------------------------------------------------------------------------------
# Install duf 0.8.1
# https://github.com/muesli/duf
# ------------------------------------------------------------------------------
RUN set -eux; \
    DUF_VERSION="0.8.1"; \
    case "${TARGETARCH}" in \
        amd64) DUF_ARCH="linux_x86_64" ;; \
        arm64) DUF_ARCH="linux_arm64" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/muesli/duf/releases/download/v${DUF_VERSION}/duf_${DUF_VERSION}_${DUF_ARCH}.tar.gz" -O duf.tar.gz; \
    tar -xzf duf.tar.gz; \
    cp duf /usr/local/bin/duf; \
    chmod +x /usr/local/bin/duf; \
    rm -rf duf.tar.gz duf; \
    duf --version

# ------------------------------------------------------------------------------
# Install sd 1.0.0
# https://github.com/chmln/sd
# ------------------------------------------------------------------------------
RUN set -eux; \
    SD_VERSION="1.0.0"; \
    case "${TARGETARCH}" in \
        amd64) SD_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) SD_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/chmln/sd/releases/download/v${SD_VERSION}/sd-v${SD_VERSION}-${SD_ARCH}.tar.gz" -O sd.tar.gz; \
    tar -xzf sd.tar.gz; \
    cp "sd-v${SD_VERSION}-${SD_ARCH}/sd" /usr/local/bin/sd; \
    chmod +x /usr/local/bin/sd; \
    rm -rf sd.tar.gz "sd-v${SD_VERSION}-${SD_ARCH}"; \
    sd --version

# ------------------------------------------------------------------------------
# Install dust 1.1.1
# https://github.com/bootandy/dust
# ------------------------------------------------------------------------------
RUN set -eux; \
    DUST_VERSION="1.1.1"; \
    case "${TARGETARCH}" in \
        amd64) DUST_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) DUST_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/bootandy/dust/releases/download/v${DUST_VERSION}/dust-v${DUST_VERSION}-${DUST_ARCH}.tar.gz" -O dust.tar.gz; \
    tar -xzf dust.tar.gz; \
    cp "dust-v${DUST_VERSION}-${DUST_ARCH}/dust" /usr/local/bin/dust; \
    chmod +x /usr/local/bin/dust; \
    rm -rf dust.tar.gz "dust-v${DUST_VERSION}-${DUST_ARCH}"; \
    dust --version

# ------------------------------------------------------------------------------
# Install procs 0.14.8
# https://github.com/dalance/procs
# ------------------------------------------------------------------------------
RUN set -eux; \
    PROCS_VERSION="0.14.8"; \
    case "${TARGETARCH}" in \
        amd64) PROCS_ARCH="x86_64-linux" ;; \
        arm64) PROCS_ARCH="aarch64-linux" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/dalance/procs/releases/download/v${PROCS_VERSION}/procs-v${PROCS_VERSION}-${PROCS_ARCH}.zip" -O procs.zip; \
    unzip -q procs.zip; \
    cp procs /usr/local/bin/procs; \
    chmod +x /usr/local/bin/procs; \
    rm -rf procs.zip procs; \
    procs --version

# ------------------------------------------------------------------------------
# Install tokei 12.1.2
# https://github.com/XAMPPRocky/tokei
# ------------------------------------------------------------------------------
RUN set -eux; \
    TOKEI_VERSION="12.1.2"; \
    case "${TARGETARCH}" in \
        amd64) TOKEI_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) TOKEI_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/XAMPPRocky/tokei/releases/download/v${TOKEI_VERSION}/tokei-${TOKEI_ARCH}.tar.gz" -O tokei.tar.gz; \
    tar -xzf tokei.tar.gz; \
    cp tokei /usr/local/bin/tokei; \
    chmod +x /usr/local/bin/tokei; \
    rm -rf tokei.tar.gz tokei; \
    tokei --version

# ------------------------------------------------------------------------------
# Install lychee 0.21.0
# https://github.com/lycheeverse/lychee
# ------------------------------------------------------------------------------
RUN set -eux; \
    LYCHEE_VERSION="0.21.0"; \
    case "${TARGETARCH}" in \
        amd64) LYCHEE_ARCH="x86_64-unknown-linux-gnu" ;; \
        arm64) LYCHEE_ARCH="aarch64-unknown-linux-gnu" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/lycheeverse/lychee/releases/download/lychee-v${LYCHEE_VERSION}/lychee-${LYCHEE_ARCH}.tar.gz" -O lychee.tar.gz || \
    wget -q "https://github.com/lycheeverse/lychee/releases/download/v${LYCHEE_VERSION}/lychee-${LYCHEE_ARCH}.tar.gz" -O lychee.tar.gz; \
    tar -xzf lychee.tar.gz; \
    cp lychee /usr/local/bin/lychee; \
    chmod +x /usr/local/bin/lychee; \
    rm -rf lychee.tar.gz lychee; \
    lychee --version

# ------------------------------------------------------------------------------
# Install actionlint 1.7.7
# https://github.com/rhysd/actionlint
# ------------------------------------------------------------------------------
RUN set -eux; \
    ACTIONLINT_VERSION="1.7.7"; \
    case "${TARGETARCH}" in \
        amd64) ACTIONLINT_ARCH="linux_amd64" ;; \
        arm64) ACTIONLINT_ARCH="linux_arm64" ;; \
        *) echo "Unsupported architecture: ${TARGETARCH}" && exit 1 ;; \
    esac; \
    wget -q "https://github.com/rhysd/actionlint/releases/download/v${ACTIONLINT_VERSION}/actionlint_${ACTIONLINT_VERSION}_${ACTIONLINT_ARCH}.tar.gz" -O actionlint.tar.gz; \
    tar -xzf actionlint.tar.gz; \
    cp actionlint /usr/local/bin/actionlint; \
    chmod +x /usr/local/bin/actionlint; \
    rm -rf actionlint.tar.gz actionlint; \
    actionlint --version

# ------------------------------------------------------------------------------
# Install PowerShell
# https://docs.microsoft.com/en-us/powershell/scripting/install/install-debian
# ------------------------------------------------------------------------------
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    set -eux; \
    wget -q https://packages.microsoft.com/config/debian/12/packages-microsoft-prod.deb -O packages-microsoft-prod.deb; \
    dpkg -i packages-microsoft-prod.deb; \
    rm packages-microsoft-prod.deb; \
    apt-get update; \
    apt-get install -y powershell; \
    apt-get autoremove -y; \
    pwsh --version

# ------------------------------------------------------------------------------
# Install Git LFS
# ------------------------------------------------------------------------------
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    set -eux; \
    apt-get update; \
    apt-get install -y git-lfs; \
    git lfs install --system; \
    apt-get autoremove -y; \
    git lfs version

# ------------------------------------------------------------------------------
# Install Python3, pip, and venv
# ------------------------------------------------------------------------------
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    set -eux; \
    apt-get update; \
    apt-get install -y python3 python3-pip python3-venv; \
    apt-get autoremove -y; \
    python3 --version; \
    pip3 --version

# ------------------------------------------------------------------------------
# Install Node.js LTS via NodeSource
# https://github.com/nodesource/distributions
# ------------------------------------------------------------------------------
RUN --mount=type=cache,target=/var/cache/apt,sharing=locked \
    --mount=type=cache,target=/var/lib/apt,sharing=locked \
    set -eux; \
    curl -fsSL https://deb.nodesource.com/setup_lts.x | bash -; \
    apt-get install -y nodejs; \
    apt-get autoremove -y; \
    node --version; \
    npm --version

# ------------------------------------------------------------------------------
# Install yamllint via pip
# --break-system-packages is required on Debian 12+ (PEP 668) to install
# Python packages system-wide outside of a virtual environment
# ------------------------------------------------------------------------------
RUN pip3 install --no-cache-dir yamllint --break-system-packages && yamllint --version

# ------------------------------------------------------------------------------
# Install pre-commit
# https://pre-commit.com
# ------------------------------------------------------------------------------
RUN pip3 install --no-cache-dir pre-commit --break-system-packages && pre-commit --version

# ------------------------------------------------------------------------------
# Configure Git Defaults for Unity-style Repositories
# ------------------------------------------------------------------------------
# Optimize git for Unity projects with large binary assets and Windows path
# lengths. These mirror the Shiro reference container.
# ------------------------------------------------------------------------------
RUN git config --system core.longpaths true \
    && git config --system core.preloadindex true \
    && git config --system gc.auto 256 \
    && git config --system lfs.concurrenttransfers 10 \
    && git config --system pack.windowMemory 256m \
    && git config --system pack.deltaCacheSize 128m

# ------------------------------------------------------------------------------
# Pre-create volume mount targets with vscode:vscode ownership
# ------------------------------------------------------------------------------
# Docker named volumes copy permissions/contents from the image ONLY on first
# attach (when the volume is empty). Pre-creating these directories with the
# correct ownership ensures the vscode user owns them on first run; rebuilds
# of an existing volume are repaired by post-create.sh / post-start.sh.
#
# Targets here MUST stay aligned with cache-contract.sh (5 entries).
# ------------------------------------------------------------------------------
RUN mkdir -p /home/vscode/.nuget \
    && mkdir -p /home/vscode/.dotnet/tools \
    && mkdir -p /home/vscode/.local/share/powershell \
    && mkdir -p /home/vscode/.cache/pip \
    && mkdir -p /workspaces/com.wallstop-studios.dxmessaging/node_modules \
    && chown -R vscode:vscode /home/vscode/.nuget \
    && chown -R vscode:vscode /home/vscode/.dotnet \
    && chown -R vscode:vscode /home/vscode/.local \
    && chown -R vscode:vscode /home/vscode/.cache \
    && chown -R vscode:vscode /workspaces

# ------------------------------------------------------------------------------
# Configure shell aliases for bash
# ------------------------------------------------------------------------------
RUN cat >> /etc/bash.bashrc <<'EOF'

# Modern CLI tool aliases
alias ls='eza --icons --group-directories-first'
alias ll='eza -la --icons --group-directories-first'
alias lt='eza --tree --icons --group-directories-first'
alias cat='bat --paging=never'
alias less='bat'
alias find='fd'
alias grep='rg'
alias df='duf'
alias sdr='sd'
alias du='dust'
alias psr='procs'

# Initialize zoxide for bash
eval "$(zoxide init bash)"
alias cd='z'

EOF

# ------------------------------------------------------------------------------
# Configure shell aliases for zsh
# ------------------------------------------------------------------------------
RUN cat >> /etc/zsh/zshrc <<'EOF'

# Modern CLI tool aliases
alias ls='eza --icons --group-directories-first'
alias ll='eza -la --icons --group-directories-first'
alias lt='eza --tree --icons --group-directories-first'
alias cat='bat --paging=never'
alias less='bat'
alias find='fd'
alias grep='rg'
alias df='duf'
alias sdr='sd'
alias du='dust'
alias psr='procs'

# Initialize zoxide for zsh
eval "$(zoxide init zsh)"
alias cd='z'

EOF

# ------------------------------------------------------------------------------
# Configure git delta as default pager
# ------------------------------------------------------------------------------
RUN git config --system core.pager "delta" && \
    git config --system interactive.diffFilter "delta --color-only" && \
    git config --system delta.navigate true && \
    git config --system delta.line-numbers true && \
    git config --system delta.side-by-side false && \
    git config --system merge.conflictstyle diff3

# ------------------------------------------------------------------------------
# Enable VS Code shell integration for bash
# ------------------------------------------------------------------------------
RUN cat >> /etc/bash.bashrc <<'EOF'

# VS Code shell integration
if [[ "$TERM_PROGRAM" == "vscode" ]]; then
    if [[ -f "$VSCODE_SHELL_INTEGRATION" ]]; then
        . "$VSCODE_SHELL_INTEGRATION"
    fi
fi

EOF

# ------------------------------------------------------------------------------
# Enable VS Code shell integration for zsh
# ------------------------------------------------------------------------------
RUN cat >> /etc/zsh/zshrc <<'EOF'

# VS Code shell integration
if [[ "$TERM_PROGRAM" == "vscode" ]]; then
    if [[ -f "$VSCODE_SHELL_INTEGRATION" ]]; then
        . "$VSCODE_SHELL_INTEGRATION"
    fi
fi

EOF

# ------------------------------------------------------------------------------
# Enable VS Code shell integration for PowerShell
# ------------------------------------------------------------------------------
RUN mkdir -p /home/vscode/.config/powershell && \
    cat > /home/vscode/.config/powershell/Microsoft.PowerShell_profile.ps1 <<'EOF'

# VS Code shell integration
if ($env:TERM_PROGRAM -eq "vscode") {
    if (Test-Path $env:VSCODE_SHELL_INTEGRATION) {
        . $env:VSCODE_SHELL_INTEGRATION
    }
}

EOF

# Fix ownership of vscode user's config directory (created as root above)
RUN chown -R vscode:vscode /home/vscode/.config

# Clean up temporary tools directory
RUN rm -rf /tmp/tools

# ------------------------------------------------------------------------------
# Install CSharpier as dotnet tool for vscode user
# ------------------------------------------------------------------------------
USER vscode
RUN dotnet tool install --global csharpier --version 0.30.6

# Add dotnet tools to PATH for vscode user
ENV PATH="${PATH}:/home/vscode/.dotnet/tools"

# Clean up
WORKDIR /workspaces
USER vscode
