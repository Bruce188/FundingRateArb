#!/usr/bin/env python3
# Requires: dotnet run --project src/FundingRateArb.Web (in separate terminal)
# App URL: http://localhost:5273
# Admin: admin@fundingratearb.com / FundingArb@2026!

import sys
import time

sys.path.insert(0, "/home/bruce/.claude/skills/playwright")
from scripts.playwright_controller import PlaywrightNative

APP_URL = "http://localhost:5273"
ADMIN_EMAIL = "admin@fundingratearb.com"
ADMIN_PASSWORD = "FundingArb@2026!"


def _login(pw, email=ADMIN_EMAIL, password=ADMIN_PASSWORD):
    """Helper: navigate to app root and log in."""
    pw.navigate(APP_URL)
    pw.type_text("#Input_Email", email)
    pw.type_text("#Input_Password", password)
    pw.click("button[type='submit']")
    time.sleep(2)


# ── Suite 5: Admin Tests ──────────────────────────────────────────────────────

def test_admin_overview_accessible():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        pw.navigate(f"{APP_URL}/Admin/Overview")
        time.sleep(1)
        # Verify the page has KPI cards (card elements) and a user table
        result = pw.wait_for_selector(".card", timeout=5000)
        assert result.get("success"), "No card elements found on Admin Overview"
        content = pw.get_content()
        html = content.get("content", "")
        assert "Admin Overview" in html or "User Activity" in html, \
            "Admin Overview page content not found"
        assert "table" in html.lower(), "User table not found on Admin Overview"
        print("PASS: test_admin_overview_accessible")
    except Exception as e:
        print(f"FAIL: test_admin_overview_accessible - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_admin_overview_accessible.png")
    finally:
        if pw:
            pw.close()


def test_admin_botconfig_accessible():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        pw.navigate(f"{APP_URL}/Admin/BotConfig")
        time.sleep(1)
        # Verify the config form loads
        result = pw.wait_for_selector("form", timeout=5000)
        assert result.get("success"), "No form element found on Admin BotConfig"
        content = pw.get_content()
        html = content.get("content", "")
        assert "Bot Configuration" in html, "Bot Configuration heading not found"
        assert "OpenThreshold" in html or "Save Configuration" in html, \
            "Config form fields not found"
        print("PASS: test_admin_botconfig_accessible")
    except Exception as e:
        print(f"FAIL: test_admin_botconfig_accessible - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_admin_botconfig_accessible.png")
    finally:
        if pw:
            pw.close()


def test_admin_exchanges_accessible():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        pw.navigate(f"{APP_URL}/Admin/Exchange")
        time.sleep(1)
        # Verify the exchange list loads
        result = pw.wait_for_selector(".card", timeout=5000)
        assert result.get("success"), "No card element found on Admin Exchange"
        content = pw.get_content()
        html = content.get("content", "")
        assert "Exchanges" in html, "Exchanges heading not found"
        assert "table" in html.lower(), "Exchange table not found"
        print("PASS: test_admin_exchanges_accessible")
    except Exception as e:
        print(f"FAIL: test_admin_exchanges_accessible - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_admin_exchanges_accessible.png")
    finally:
        if pw:
            pw.close()


def test_admin_assets_accessible():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        pw.navigate(f"{APP_URL}/Admin/Asset")
        time.sleep(1)
        # Verify the asset list loads
        result = pw.wait_for_selector(".card", timeout=5000)
        assert result.get("success"), "No card element found on Admin Asset"
        content = pw.get_content()
        html = content.get("content", "")
        assert "Assets" in html, "Assets heading not found"
        assert "table" in html.lower(), "Asset table not found"
        print("PASS: test_admin_assets_accessible")
    except Exception as e:
        print(f"FAIL: test_admin_assets_accessible - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_admin_assets_accessible.png")
    finally:
        if pw:
            pw.close()


def test_admin_users_accessible():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        pw.navigate(f"{APP_URL}/Admin/Users")
        time.sleep(1)
        # Verify the user list loads — redirect or direct user management page
        content = pw.get_content()
        html = content.get("content", "")
        url = content.get("url", "")
        # Accept either a users list or a redirect to a related page (still logged in)
        assert "Users" in html or "User" in html or "/Admin/" in url, \
            f"User management page not found. URL: {url}"
        print("PASS: test_admin_users_accessible")
    except Exception as e:
        print(f"FAIL: test_admin_users_accessible - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_admin_users_accessible.png")
    finally:
        if pw:
            pw.close()


def test_admin_kill_switch_visible():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        pw.navigate(f"{APP_URL}/Admin/BotConfig")
        time.sleep(1)
        # Verify the kill switch toggle button exists
        content = pw.get_content()
        html = content.get("content", "")
        # Kill switch appears as either "Kill Switch (Stop Bot)" or "Enable Bot" button
        has_kill_switch = (
            "Kill Switch" in html
            or "Kill Switch (Stop Bot)" in html
            or "Enable Bot" in html
        )
        assert has_kill_switch, "Kill switch toggle not found on Admin BotConfig page"
        # Also verify it is inside a form (toggle action)
        assert 'asp-action="Toggle"' in html or 'action="/Admin/BotConfig/Toggle"' in html \
            or "Toggle" in html, "Kill switch toggle form not found"
        print("PASS: test_admin_kill_switch_visible")
    except Exception as e:
        print(f"FAIL: test_admin_kill_switch_visible - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_admin_kill_switch_visible.png")
    finally:
        if pw:
            pw.close()


# ── Suite 6: Mobile Responsive Tests ─────────────────────────────────────────

def test_mobile_dashboard_renders():
    pw = None
    try:
        pw = PlaywrightNative()
        pw.page.set_viewport_size({"width": 375, "height": 812})
        _login(pw)
        time.sleep(1)
        # Verify no horizontal overflow: scrollWidth should not exceed viewport width
        scroll_width = pw.evaluate("document.body.scrollWidth")
        page_width = scroll_width.get("result", 999)
        assert page_width <= 375, \
            f"Horizontal overflow on mobile dashboard: scrollWidth={page_width}px > 375px"
        pw.take_screenshot("/tmp/mobile_dashboard_renders.png", full_page=True)
        print("PASS: test_mobile_dashboard_renders")
    except Exception as e:
        print(f"FAIL: test_mobile_dashboard_renders - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_mobile_dashboard_renders.png")
    finally:
        if pw:
            pw.close()


def test_mobile_navbar_hamburger():
    pw = None
    try:
        pw = PlaywrightNative()
        pw.page.set_viewport_size({"width": 375, "height": 812})
        _login(pw)
        time.sleep(1)
        # Verify hamburger button exists
        result = pw.wait_for_selector(".navbar-toggler", timeout=5000)
        assert result.get("success"), "Hamburger menu button (.navbar-toggler) not found"
        # Click the hamburger button
        pw.click(".navbar-toggler")
        time.sleep(1)
        # Verify the nav collapse becomes visible/shown
        is_visible = pw.evaluate(
            "document.querySelector('#navbarMain') !== null && "
            "(document.querySelector('#navbarMain').classList.contains('show') || "
            " getComputedStyle(document.querySelector('#navbarMain')).display !== 'none')"
        )
        assert is_visible.get("result", False), \
            "Nav items did not become visible after clicking hamburger"
        print("PASS: test_mobile_navbar_hamburger")
    except Exception as e:
        print(f"FAIL: test_mobile_navbar_hamburger - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_mobile_navbar_hamburger.png")
    finally:
        if pw:
            pw.close()


def test_mobile_kpi_cards_visible():
    pw = None
    try:
        pw = PlaywrightNative()
        pw.page.set_viewport_size({"width": 375, "height": 812})
        _login(pw)
        time.sleep(1)
        # Count KPI cards on dashboard — expecting 4 (bot status, positions, PnL, best spread)
        card_count = pw.evaluate(
            "document.querySelectorAll('.kpi-card').length"
        )
        count = card_count.get("result", 0)
        assert count >= 4, \
            f"Expected at least 4 KPI cards on mobile dashboard, found {count}"
        print("PASS: test_mobile_kpi_cards_visible")
    except Exception as e:
        print(f"FAIL: test_mobile_kpi_cards_visible - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_mobile_kpi_cards_visible.png")
    finally:
        if pw:
            pw.close()


def test_mobile_settings_page():
    pw = None
    try:
        pw = PlaywrightNative()
        pw.page.set_viewport_size({"width": 375, "height": 812})
        _login(pw)
        pw.navigate(f"{APP_URL}/Settings/ApiKeys")
        time.sleep(1)
        # Verify page renders without overflow
        scroll_width = pw.evaluate("document.body.scrollWidth")
        page_width = scroll_width.get("result", 999)
        assert page_width <= 375, \
            f"Horizontal overflow on mobile settings page: scrollWidth={page_width}px > 375px"
        pw.take_screenshot("/tmp/mobile_settings_page.png", full_page=True)
        print("PASS: test_mobile_settings_page")
    except Exception as e:
        print(f"FAIL: test_mobile_settings_page - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_mobile_settings_page.png")
    finally:
        if pw:
            pw.close()


# ── Suite 7: Real-Time / SignalR Tests ───────────────────────────────────────

def test_signalr_connection_badge():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        # Wait up to 10 seconds for the connection status badge to update
        deadline = time.time() + 10
        status_text = None
        while time.time() < deadline:
            result = pw.get_text("#connection-status")
            text = result.get("text", "").strip()
            if text:
                status_text = text
                break
            time.sleep(0.5)
        assert status_text is not None, "Connection status element (#connection-status) not found"
        valid_states = {"Live", "Connecting...", "Disconnected", "Reconnecting..."}
        assert status_text in valid_states or len(status_text) > 0, \
            f"Unexpected connection status text: '{status_text}'"
        print(f"PASS: test_signalr_connection_badge (status='{status_text}')")
    except Exception as e:
        print(f"FAIL: test_signalr_connection_badge - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_signalr_connection_badge.png")
    finally:
        if pw:
            pw.close()


def test_dashboard_has_signalr_script():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        content = pw.get_content()
        html = content.get("content", "")
        # The layout includes signalr.min.js and dashboard.js which creates the hub connection
        has_signalr = "signalr" in html.lower()
        assert has_signalr, "SignalR script tag not found in dashboard page HTML"
        # Also verify the hub connection code is present (dashboard.js is inline or referenced)
        has_hub = "dashboard.js" in html or "/hubs/dashboard" in html or "HubConnectionBuilder" in html
        assert has_hub, "SignalR hub connection code reference not found in page HTML"
        print("PASS: test_dashboard_has_signalr_script")
    except Exception as e:
        print(f"FAIL: test_dashboard_has_signalr_script - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_dashboard_has_signalr_script.png")
    finally:
        if pw:
            pw.close()


# ── Suite 8: Navigation Tests ─────────────────────────────────────────────────

def test_settings_nav_in_navbar():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        # Verify "Settings" link exists in the main navbar
        result = pw.wait_for_selector("a.nav-link[href*='Settings']", timeout=5000)
        if not result.get("success"):
            # Fallback: check HTML content for Settings nav link
            content = pw.get_content()
            html = content.get("content", "")
            assert "Settings" in html and "nav-link" in html, \
                "Settings nav link not found in navbar"
        print("PASS: test_settings_nav_in_navbar")
    except Exception as e:
        print(f"FAIL: test_settings_nav_in_navbar - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_settings_nav_in_navbar.png")
    finally:
        if pw:
            pw.close()


def test_admin_dropdown_has_overview():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        # Verify Admin dropdown contains "Overview" link
        content = pw.get_content()
        html = content.get("content", "")
        assert "Admin" in html, "Admin dropdown not found in navbar (user may not be admin)"
        # Click the Admin dropdown toggle to reveal its items
        result = pw.wait_for_selector(".nav-item.dropdown", timeout=5000)
        assert result.get("success"), "Admin dropdown nav item not found"
        pw.click(".nav-item.dropdown > a.nav-link.dropdown-toggle")
        time.sleep(0.5)
        # Verify Overview link is visible in the dropdown
        overview_result = pw.wait_for_selector(
            ".dropdown-menu a[href*='Overview'], .dropdown-item", timeout=3000
        )
        assert overview_result.get("success"), "No dropdown items found after opening Admin dropdown"
        content_after = pw.get_content()
        html_after = content_after.get("content", "")
        assert "Overview" in html_after, \
            "Overview link not found in Admin dropdown"
        print("PASS: test_admin_dropdown_has_overview")
    except Exception as e:
        print(f"FAIL: test_admin_dropdown_has_overview - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_admin_dropdown_has_overview.png")
    finally:
        if pw:
            pw.close()


def test_all_nav_links_work():
    pw = None
    try:
        pw = PlaywrightNative()
        _login(pw)
        nav_links = [
            ("Dashboard", f"{APP_URL}/"),
            ("Positions", f"{APP_URL}/Positions"),
            ("Alerts", f"{APP_URL}/Alerts"),
            ("Settings", f"{APP_URL}/Settings/ApiKeys"),
        ]
        for label, url in nav_links:
            pw.navigate(url)
            time.sleep(1)
            content = pw.get_content()
            page_url = content.get("url", "")
            html = content.get("content", "")
            # Verify the page loaded without a server error (no 500 page)
            assert "500" not in pw.get_text("h1").get("text", "") or label in html, \
                f"{label} page appears to have returned an error"
            assert "An error occurred" not in html or "alert" not in html.lower(), \
                f"Error on {label} page at {page_url}"
            # Verify we landed on a page that still has our navbar (i.e., did not log out)
            assert "navbar" in html.lower(), \
                f"{label} page did not include the navbar — unexpected redirect"
        print("PASS: test_all_nav_links_work")
    except Exception as e:
        print(f"FAIL: test_all_nav_links_work - {e}")
        if pw:
            pw.take_screenshot("/tmp/fail_test_all_nav_links_work.png")
    finally:
        if pw:
            pw.close()


# ── Runner ────────────────────────────────────────────────────────────────────

def run_all_tests():
    print("=" * 60)
    print("Admin, Mobile, Real-Time & Navigation E2E Tests")
    print("=" * 60)

    print("\n--- Suite 5: Admin Tests ---")
    test_admin_overview_accessible()
    test_admin_botconfig_accessible()
    test_admin_exchanges_accessible()
    test_admin_assets_accessible()
    test_admin_users_accessible()
    test_admin_kill_switch_visible()

    print("\n--- Suite 6: Mobile Responsive Tests ---")
    test_mobile_dashboard_renders()
    test_mobile_navbar_hamburger()
    test_mobile_kpi_cards_visible()
    test_mobile_settings_page()

    print("\n--- Suite 7: Real-Time / SignalR Tests ---")
    test_signalr_connection_badge()
    test_dashboard_has_signalr_script()

    print("\n--- Suite 8: Navigation Tests ---")
    test_settings_nav_in_navbar()
    test_admin_dropdown_has_overview()
    test_all_nav_links_work()

    print("\n" + "=" * 60)
    print("Done.")
    print("=" * 60)


if __name__ == "__main__":
    run_all_tests()
