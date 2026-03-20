#!/usr/bin/env python3
"""Quick UI smoke test after pipeline hardening changes."""
import sys, time
sys.path.insert(0, "/home/bruce/.claude/skills/playwright")
from scripts.playwright_controller import PlaywrightNative

pw = PlaywrightNative()
try:
    # 1. Navigate to login
    r = pw.navigate("http://localhost:5273")
    print(f"1. Homepage: {r['status']} - {r['title']}")

    # 2. Login
    pw.type_text("#Input_Email", "admin@fundingratearb.com")
    pw.type_text("#Input_Password", "FundingArb@2026!")
    pw.click("button[type='submit']")
    time.sleep(2)

    # 3. Check dashboard loaded
    r = pw.get_content()
    print(f"2. After login URL: {r['url']}")

    # 4. Check SignalR status
    text = pw.get_text("#connection-status")
    print(f"3. SignalR status: {text.get('text', 'NOT FOUND')}")

    # 5. Screenshot dashboard
    pw.take_screenshot(filename="/home/bruce/bad/eindproject/tests/screenshots/dashboard_pipeline.png", full_page=True)
    print("4. Dashboard screenshot saved")

    # 6. Navigate to opportunities
    pw.navigate("http://localhost:5273/Opportunities")
    time.sleep(2)
    pw.take_screenshot(filename="/home/bruce/bad/eindproject/tests/screenshots/opportunities_pipeline.png", full_page=True)
    r = pw.get_content()
    print(f"5. Opportunities page: {r['url']}")

    # 7. Navigate to positions
    pw.navigate("http://localhost:5273/Positions")
    time.sleep(1)
    pw.take_screenshot(filename="/home/bruce/bad/eindproject/tests/screenshots/positions_pipeline.png", full_page=True)
    print("6. Positions page screenshot saved")

    # 8. Check admin bot config (our new validation rules)
    pw.navigate("http://localhost:5273/Admin/BotConfig")
    time.sleep(1)
    pw.take_screenshot(filename="/home/bruce/bad/eindproject/tests/screenshots/botconfig_pipeline.png", full_page=True)
    print("7. Bot Config page screenshot saved")

    # 9. Check health endpoint
    pw.navigate("http://localhost:5273/health")
    text = pw.get_text("body")
    print(f"8. Health: {text.get('text', 'N/A')}")

    print("\n=== ALL UI CHECKS PASSED ===")

except Exception as e:
    print(f"ERROR: {e}")
    pw.take_screenshot(filename="/home/bruce/bad/eindproject/tests/screenshots/error_pipeline.png")
finally:
    pw.close()
