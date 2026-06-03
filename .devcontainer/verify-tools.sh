#!/usr/bin/env bash
# verify-tools.sh - Verify all development container tools are installed and working
# Run this script to check that all expected tools are available in the container

set -e

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
NC='\033[0m' # No Color

# Counters
PASS=0
FAIL=0
WARN=0

# Print header
echo -e "${BLUE}=======================================================${NC}"
echo -e "${BLUE}  DxMessaging Dev Container - Tool Verification${NC}"
echo -e "${BLUE}=======================================================${NC}"
echo ""

# Function to check if a command exists and get its version
check_tool() {
    local name="$1"
    local cmd="$2"
    local version_flag="${3:---version}"
    
    printf "%-20s" "$name"
    
    if command -v "$cmd" &> /dev/null; then
        local version
        version=$("$cmd" $version_flag 2>&1 | head -n 1) || version="installed"
        echo -e "${GREEN}✓${NC} $version"
        ((++PASS))
        return 0
    else
        echo -e "${RED}✗ NOT FOUND${NC}"
        ((++FAIL))
        return 1
    fi
}

# Function to check if a tool exists without version check
check_tool_exists() {
    local name="$1"
    local cmd="$2"
    
    printf "%-20s" "$name"
    
    if command -v "$cmd" &> /dev/null; then
        echo -e "${GREEN}✓${NC} available"
        ((++PASS))
        return 0
    else
        echo -e "${RED}✗ NOT FOUND${NC}"
        ((++FAIL))
        return 1
    fi
}

# Function to check optional tool
check_optional() {
    local name="$1"
    local cmd="$2"
    local version_flag="${3:---version}"
    
    printf "%-20s" "$name"
    
    if command -v "$cmd" &> /dev/null; then
        local version
        version=$("$cmd" $version_flag 2>&1 | head -n 1) || version="installed"
        echo -e "${GREEN}✓${NC} $version"
        ((++PASS))
        return 0
    else
        echo -e "${YELLOW}○ not found (optional)${NC}"
        ((++WARN))
        return 1
    fi
}

echo -e "${BLUE}=== Core Development Tools ===${NC}"
check_tool "dotnet" "dotnet" "--version"
check_tool "node" "node" "--version"
check_tool "npm" "npm" "--version"
check_tool "python3" "python3" "--version"
check_tool "pip3" "pip3" "--version"
check_tool "git" "git" "--version"
check_tool "git-lfs" "git-lfs" "version"
check_tool "powershell" "pwsh" "--version"

echo ""
echo -e "${BLUE}=== Shell Utilities ===${NC}"
check_tool "bash" "bash" "--version"
check_tool "zsh" "zsh" "--version"

echo ""
echo -e "${BLUE}=== Modern CLI Replacements ===${NC}"
check_tool "eza (ls)" "eza" "--version"
check_tool "bat (cat)" "bat" "--version"
check_tool "fd (find)" "fd" "--version"
check_tool "ripgrep (grep)" "rg" "--version"
check_tool "delta (diff)" "delta" "--version"
check_tool "duf (df)" "duf" "--version"
check_tool "sd (sed)" "sd" "--version"
check_tool "dust (du)" "dust" "--version"
check_tool "procs (ps)" "procs" "--version"
check_tool "zoxide (cd)" "zoxide" "--version"

echo ""
echo -e "${BLUE}=== Search & Navigation ===${NC}"
check_tool "fzf" "fzf" "--version"
check_tool "ag (silver)" "ag" "--version"
# tree uses different version flags on different systems
printf "%-20s" "tree"
if command -v tree &> /dev/null; then
    version=$(tree --version 2>&1 | head -n 1) || version=$(tree -V 2>&1 | head -n 1) || version="installed"
    echo -e "${GREEN}✓${NC} $version"
    ((++PASS))
else
    echo -e "${RED}✗ NOT FOUND${NC}"
    ((++FAIL))
fi

echo ""
echo -e "${BLUE}=== Code Analysis ===${NC}"
check_tool "tokei" "tokei" "--version"
check_tool "shellcheck" "shellcheck" "--version"
check_tool "pre-commit" "pre-commit" "--version"

echo ""
echo -e "${BLUE}=== YAML/JSON Tools ===${NC}"
check_tool "yq" "yq" "--version"
check_tool "jq" "jq" "--version"
check_tool "yamllint" "yamllint" "--version"

echo ""
echo -e "${BLUE}=== Link & Workflow Checking ===${NC}"
check_tool "lychee" "lychee" "--version"
check_tool "actionlint" "actionlint" "--version"

echo ""
echo -e "${BLUE}=== System Monitoring ===${NC}"
check_tool "htop" "htop" "--version"
check_tool "ncdu" "ncdu" "--version"

echo ""
echo -e "${BLUE}=== .NET Tools ===${NC}"
check_tool "csharpier" "csharpier" "--version"

echo ""
echo -e "${BLUE}=== AI / Agent CLIs ===${NC}"
check_optional "codex (@openai)" "codex" "--version"

echo ""
echo -e "${BLUE}=== npm Configuration ===${NC}"
printf "%-20s" "npm prefix"
npm_prefix=$(npm config get prefix 2>/dev/null || echo "error")
if [ "$npm_prefix" = "$HOME/.local" ]; then
    echo -e "${GREEN}✓${NC} $npm_prefix"
    ((++PASS))
else
    echo -e "${RED}✗${NC} $npm_prefix (expected $HOME/.local)"
    ((++FAIL))
fi

echo ""
echo -e "${BLUE}=== Moreutils ===${NC}"
check_tool_exists "sponge" "sponge"
check_tool_exists "ts" "ts"
check_tool_exists "parallel" "parallel"
check_tool_exists "ifne" "ifne"

echo ""
echo -e "${BLUE}=== Misc Utilities ===${NC}"
check_tool_exists "tldr" "tldr"
check_tool_exists "unzip" "unzip"
check_tool_exists "wget" "wget"
check_tool_exists "curl" "curl"

echo ""
echo -e "${BLUE}=======================================================${NC}"
echo -e "${BLUE}  Summary${NC}"
echo -e "${BLUE}=======================================================${NC}"
echo -e "  ${GREEN}Passed:${NC}   $PASS"
echo -e "  ${RED}Failed:${NC}   $FAIL"
echo -e "  ${YELLOW}Warnings:${NC} $WARN"
echo ""

if [ $FAIL -eq 0 ]; then
    echo -e "${GREEN}All required tools are installed and working!${NC}"
    exit 0
else
    echo -e "${RED}Some required tools are missing. Please check the Dockerfile.${NC}"
    exit 1
fi
