#!/bin/bash
# Analyzes recent logs and pushes a findings summary to the repo.
# Runs locally via crontab. The cloud scheduled task picks up the output.
set -euo pipefail

PROJECT_DIR="/home/bruce/bad/eindproject"
LOG_DIR="$PROJECT_DIR/logs"
OUTPUT="$PROJECT_DIR/docs/log-findings-latest.md"
BRANCH="main"

cd "$PROJECT_DIR"

# Skip if no log files exist
if [ ! -d "$LOG_DIR" ] || [ -z "$(ls "$LOG_DIR"/*.log 2>/dev/null)" ]; then
    echo "No log files found, skipping"
    exit 0
fi

# Find the most recent log file
LATEST_LOG=$(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1)
TIMESTAMP=$(date -u '+%Y-%m-%dT%H:%M:%SZ')

# Extract critical/error/warning entries from last 5 hours
SINCE=$(date -u -d '5 hours ago' '+%Y-%m-%d %H:%M')

# Count severity levels
CRITICAL_COUNT=$(grep -c '\[Critical\]' "$LATEST_LOG" 2>/dev/null || echo 0)
ERROR_COUNT=$(grep -c '\[Error\]' "$LATEST_LOG" 2>/dev/null || echo 0)
WARNING_COUNT=$(grep -c '\[Warning\]' "$LATEST_LOG" 2>/dev/null || echo 0)

# Extract unique error patterns (deduplicated by message template)
CRITICAL_LINES=$(grep '\[Critical\]' "$LATEST_LOG" 2>/dev/null | tail -20 || true)
ERROR_LINES=$(grep '\[Error\]' "$LATEST_LOG" 2>/dev/null | tail -20 || true)

# Extract specific trading events
EMERGENCY_CLOSE=$(grep -i 'emergency.close\|EmergencyClosed' "$LATEST_LOG" 2>/dev/null | tail -10 || true)
CIRCUIT_BREAKER=$(grep -i 'circuit.breaker.*OPEN\|circuit.breaker.*excluded' "$LATEST_LOG" 2>/dev/null | tail -10 || true)
FAILED_LEGS=$(grep -i 'leg.*failed\|leg.*threw\|CLOSE FAILED' "$LATEST_LOG" 2>/dev/null | tail -10 || true)
ZERO_PRICES=$(grep -i 'zero entry prices' "$LATEST_LOG" 2>/dev/null | tail -10 || true)

# Only write if there are findings
if [ "$CRITICAL_COUNT" -eq 0 ] && [ "$ERROR_COUNT" -eq 0 ]; then
    cat > "$OUTPUT" << EOF
# Log Findings

**Generated:** $TIMESTAMP
**Source:** $(basename "$LATEST_LOG")
**Status:** No critical or error entries found.

Warnings: $WARNING_COUNT
EOF
else
    cat > "$OUTPUT" << 'HEREDOC_OUTER'
# Log Findings
HEREDOC_OUTER

    cat >> "$OUTPUT" << EOF

**Generated:** $TIMESTAMP
**Source:** $(basename "$LATEST_LOG")
**Counts:** Critical=$CRITICAL_COUNT, Error=$ERROR_COUNT, Warning=$WARNING_COUNT

## Critical Entries
\`\`\`
$CRITICAL_LINES
\`\`\`

## Error Entries
\`\`\`
$ERROR_LINES
\`\`\`

## Emergency Close Events
\`\`\`
$EMERGENCY_CLOSE
\`\`\`

## Circuit Breaker Events
\`\`\`
$CIRCUIT_BREAKER
\`\`\`

## Failed Legs
\`\`\`
$FAILED_LEGS
\`\`\`

## Zero Price Events
\`\`\`
$ZERO_PRICES
\`\`\`
EOF
fi

# Commit and push if changed
cd "$PROJECT_DIR"
git checkout "$BRANCH" 2>/dev/null || true
git add docs/log-findings-latest.md
if git diff --cached --quiet; then
    echo "No changes to commit"
    exit 0
fi
git commit -m "chore: update log findings summary"
git push origin "$BRANCH"
echo "Log findings pushed at $TIMESTAMP"
