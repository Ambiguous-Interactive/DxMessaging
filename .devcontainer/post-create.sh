#!/usr/bin/env bash
# shellcheck shell=bash
# =============================================================================
# DxMessaging Devcontainer - Post-Create Bootstrap
# =============================================================================
# Runs once after the devcontainer is created. Performs initial setup and
# validation of the development environment.
# =============================================================================

set -euo pipefail

# -----------------------------------------------------------------------------
# Configuration
# -----------------------------------------------------------------------------
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
WORKSPACE_DIR="$(cd "${SCRIPT_DIR}/.." && pwd)"
LOG_PREFIX="[post-create]"

if [[ -t 1 ]]; then
    BLUE='\033[0;34m'
    GREEN='\033[0;32m'
    YELLOW='\033[1;33m'
    RED='\033[0;31m'
    NC='\033[0m'
else
    BLUE=''
    GREEN=''
    YELLOW=''
    RED=''
    NC=''
fi

# -----------------------------------------------------------------------------
# Helper Functions
# -----------------------------------------------------------------------------

log_info() {
    echo -e "${BLUE}${LOG_PREFIX}${NC} $1"
}

log_success() {
    echo -e "${GREEN}${LOG_PREFIX} ✓${NC} $1"
}

log_warning() {
    echo -e "${YELLOW}${LOG_PREFIX} ⚠${NC} $1"
}

log_error() {
    echo -e "${RED}${LOG_PREFIX} ✗${NC} $1" >&2
}

log_header() {
    echo ""
    echo -e "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${BLUE}  $1${NC}"
    echo -e "${BLUE}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
}

fail() {
    log_error "$1"
    exit 1
}

run_optional() {
    local label="$1"
    shift

    log_info "$label"
    if "$@"; then
        log_success "$label completed"
    else
        log_warning "$label failed (continuing)"
    fi
}

ensure_path_line() {
    local rc_file="$1"
    # The $HOME literal is intentional — it must be written to the rc file
    # unexpanded so it expands in the user's shell at source time.
    # shellcheck disable=SC2016
    local path_line='export PATH="$HOME/.local/bin:$PATH"'

    if [[ ! -f "$rc_file" ]]; then
        return
    fi

    if ! grep -Fqx "$path_line" "$rc_file"; then
        {
            echo ""
            echo "# Ensure npm user-global binaries are available"
            echo "$path_line"
        } >> "$rc_file"
    fi
}

# -----------------------------------------------------------------------------
# Source the cache contract (FATAL if missing — same pattern as Shiro)
# -----------------------------------------------------------------------------
if [[ ! -f "${SCRIPT_DIR}/cache-contract.sh" ]]; then
    fail "cache-contract.sh not found at ${SCRIPT_DIR}/cache-contract.sh"
fi

# shellcheck source=.devcontainer/cache-contract.sh
source "${SCRIPT_DIR}/cache-contract.sh" || fail "failed to source cache-contract.sh"

# -----------------------------------------------------------------------------
# Volume Mount Permissions
# -----------------------------------------------------------------------------

fix_volume_permissions() {
    log_header "Fixing Volume Mount Permissions"

    if ! cache_contract_validate_shape; then
        log_error "Cache mount contract is invalid (sources/targets length mismatch)."
        return 1
    fi

    local current_uid
    local current_gid
    current_uid="$(id -u)"
    current_gid="$(id -g)"

    # Docker named volumes only inherit ownership from image content on first
    # attach. Existing named volumes may be root-owned after rebuilds, so
    # verify and fix each mount target.
    for i in "${!CACHE_MOUNT_TARGETS[@]}"; do
        local source_name="${CACHE_MOUNT_SOURCES[$i]}"
        local target_dir="${CACHE_MOUNT_TARGETS[$i]}"

        mkdir -p "${target_dir}" 2>/dev/null || true

        local owner_uid
        owner_uid="$(cache_contract_get_owner_uid "${target_dir}" 2>/dev/null || echo "unknown")"
        if [[ "${owner_uid}" != "${current_uid}" ]]; then
            log_info "Fixing ownership of ${target_dir} (source=${source_name}, owner=${owner_uid}, expected=${current_uid})..."
            if sudo chown -R "${current_uid}:${current_gid}" "${target_dir}" 2>/dev/null; then
                owner_uid="$(cache_contract_get_owner_uid "${target_dir}" 2>/dev/null || echo "unknown")"
            else
                log_warning "Could not fix ownership of ${target_dir}"
            fi
        fi

        if [[ "${owner_uid}" == "${current_uid}" ]]; then
            log_success "${target_dir} ownership OK (source=${source_name}, uid=${owner_uid})"
        else
            log_error "${target_dir} ownership remains ${owner_uid} (expected ${current_uid}); sudo chown appears to have failed silently"
        fi
    done
}

# -----------------------------------------------------------------------------
# Docker Socket Verification (warn-only — DooD is optional for .NET-only flow)
# -----------------------------------------------------------------------------

verify_docker_socket() {
    log_header "Verifying Docker Socket (DooD)"

    if ! command -v docker >/dev/null 2>&1; then
        log_warning "docker CLI not found in container."
        log_warning "  Remediation: ensure host Docker is running and the devcontainer was built"
        log_warning "  with the 'docker-outside-of-docker' feature enabled (see devcontainer.json)."
        return 0
    fi

    if docker info >/dev/null 2>&1; then
        log_success "Docker socket reachable; Phase 2+ Unity test runner can spawn containers."
    else
        log_warning "docker info failed — socket not accessible from inside the container."
        log_warning "  Remediation: ensure host Docker is running and the devcontainer was built"
        log_warning "  with the 'docker-outside-of-docker' feature enabled (see devcontainer.json)."
        log_warning "  .NET-only workflows will continue; Unity test workflows will fail in Phase 2+."
    fi

    return 0
}

# -----------------------------------------------------------------------------
# .NET Configuration
# -----------------------------------------------------------------------------

validate_dotnet() {
    log_header "Validating .NET SDKs"

    mkdir -p "${HOME}/.dotnet/tools"

    if ! command -v dotnet >/dev/null 2>&1; then
        log_error ".NET SDK not found!"
        return 1
    fi

    local dotnet_version
    dotnet_version="$(dotnet --version 2>/dev/null || echo unknown)"
    log_success "Active .NET SDK: ${dotnet_version}"

    log_info "Installed .NET SDKs:"
    dotnet --list-sdks | while read -r line; do
        echo "        $line"
    done

    if dotnet --list-sdks | grep -q "^9\.[0-9]\+\."; then
        log_success ".NET 9 SDK found"
    else
        log_warning ".NET 9 SDK not detected (expected from base image)."
    fi

    if dotnet --list-sdks | grep -q "^10\.[0-9]\+\."; then
        log_success ".NET 10 SDK found"
    else
        log_error ".NET 10 SDK not detected (C# Dev Kit requires it)."
        return 1
    fi

    return 0
}

# -----------------------------------------------------------------------------
# Workspace Validation (UPM package — no Assets/ or ProjectSettings/ at root)
# -----------------------------------------------------------------------------

validate_workspace() {
    log_header "Validating Workspace"

    cd "${WORKSPACE_DIR}"

    log_info "Checking DxMessaging UPM package structure..."

    local checks_passed=0
    local checks_total=0

    ((++checks_total))
    if [[ -f "package.json" ]]; then
        log_success "package.json found"
        ((++checks_passed))
    else
        log_warning "package.json not found"
    fi

    ((++checks_total))
    if [[ -d "Editor" ]]; then
        log_success "Editor/ folder found"
        ((++checks_passed))
    else
        log_warning "Editor/ folder not found"
    fi

    ((++checks_total))
    if [[ -d "Runtime" ]]; then
        log_success "Runtime/ folder found"
        ((++checks_passed))
    else
        log_warning "Runtime/ folder not found"
    fi

    ((++checks_total))
    if [[ -d "Tests" ]]; then
        log_success "Tests/ folder found"
        ((++checks_passed))
    else
        log_warning "Tests/ folder not found"
    fi

    echo ""
    log_info "Workspace validation: ${checks_passed}/${checks_total} checks passed"

    return 0
}

# -----------------------------------------------------------------------------
# Environment Summary
# -----------------------------------------------------------------------------

print_summary() {
    log_header "Development Environment Ready"

    echo ""
    echo "  Project:           DxMessaging (Unity UPM package)"
    echo "  Workspace:         ${WORKSPACE_DIR}"
    echo ""
    echo "  Available Tools:"
    echo "    .NET SDK:        $(dotnet --version 2>/dev/null || echo 'N/A')"
    # shellcheck disable=SC2016 # $PSVersionTable is PowerShell, not bash, so single-quote it.
    echo "    PowerShell:      $(pwsh -NoProfile -Command '$PSVersionTable.PSVersion.ToString()' 2>/dev/null || echo 'N/A')"
    echo "    Python:          $(python3 --version 2>/dev/null | cut -d' ' -f2 || echo 'N/A')"
    echo "    Node.js:         $(node --version 2>/dev/null || echo 'N/A')"
    echo "    GitHub CLI:      $(gh --version 2>/dev/null | head -n1 | cut -d' ' -f3 || echo 'N/A')"
    echo "    Git LFS:         $(git lfs version 2>/dev/null | cut -d' ' -f1-2 || echo 'N/A')"
    echo "    Docker (DooD):   $(docker --version 2>/dev/null || echo 'N/A — DooD not active')"
    echo ""
    echo "  Quick Commands:"
    echo "    dotnet test                          # Run .NET tests"
    echo "    dotnet csharpier .                   # Format C# sources"
    echo "    npm run preflight:pre-commit         # Run repo preflight checks"
    echo "    pre-commit run --all-files           # Run all pre-commit hooks"
    echo "    bash .devcontainer/validate-caching.sh   # Validate cache mount contract"
    echo "    # bash scripts/unity/run-tests.sh --platform editmode  (Phase 2)"
    echo ""
    log_success "Environment setup complete!"
    echo ""

    return 0
}

# -----------------------------------------------------------------------------
# Main Execution
# -----------------------------------------------------------------------------

main() {
    local exit_code=0

    echo ""
    echo -e "${BLUE}╔════════════════════════════════════════════════════════════╗${NC}"
    echo -e "${BLUE}║      DxMessaging Devcontainer - Post-Create Bootstrap     ║${NC}"
    echo -e "${BLUE}╚════════════════════════════════════════════════════════════╝${NC}"
    echo ""

    # Step 1: fix volume permissions FIRST so subsequent writes (npm, dotnet,
    # pip, etc.) hit a writable home directory.
    if ! fix_volume_permissions; then
        log_error "Volume permission fix failed; cannot continue safely."
        return 1
    fi

    # Step 2: warn (don't fail) if the docker socket isn't accessible.
    verify_docker_socket || true

    # Step 3: configure npm prefix for non-root global installs.
    log_header "Configuring npm Global Prefix"
    mkdir -p "$HOME/.local/bin"
    log_info "Setting npm prefix to $HOME/.local"
    npm config set prefix "$HOME/.local"

    local current_prefix
    current_prefix="$(npm config get prefix)"
    if [[ "$current_prefix" != "$HOME/.local" ]]; then
        fail "npm prefix is '$current_prefix', expected '$HOME/.local'"
    fi
    log_success "npm prefix configured: $current_prefix"

    export PATH="$HOME/.local/bin:$PATH"
    ensure_path_line "$HOME/.bashrc"
    ensure_path_line "$HOME/.zshrc"

    # Step 4: workspace bootstrap.
    log_header "Bootstrapping Workspace"
    local workspace_dir
    workspace_dir="${containerWorkspaceFolder:-${WORKSPACE_DIR}}"

    cd "${WORKSPACE_DIR}"

    run_optional "Restoring .NET local tools" dotnet tool restore
    run_optional "Installing workspace npm dependencies" npm install
    run_optional "Configuring git safe.directory" git config --global --add safe.directory "$workspace_dir"
    run_optional "Installing pre-commit hook environments" node scripts/ensure-pre-commit.js install-hooks

    # Step 5: validate environment (warn-only, never blocking).
    validate_dotnet || { log_error ".NET validation failed"; exit_code=1; }
    validate_workspace || { log_error "Workspace validation failed"; exit_code=1; }

    print_summary

    return "${exit_code}"
}

main "$@"
