"""Shared fixtures for Playwright E2E tests.

Prerequisites:
  - App running at http://localhost:5273
  - SQL Server container running (docker start sqlserver-dev)
  - Admin seeded: admin@fundingratearb.com / FundingArb@2026!

Run:
  cd tests/playwright && python3 -m pytest -v
"""

import os
import time
import urllib.request

import pytest
from playwright.sync_api import sync_playwright

APP_URL = "http://localhost:5273"
ADMIN_EMAIL = "admin@fundingratearb.com"
ADMIN_PASSWORD = "FundingArb@2026!"

SCREENSHOT_DIR = os.path.join(os.path.dirname(__file__), "screenshots")


# ---------------------------------------------------------------------------
# Hooks
# ---------------------------------------------------------------------------

@pytest.hookimpl(tryfirst=True, hookwrapper=True)
def pytest_runtest_makereport(item, call):
    """Attach test outcome to the item so fixtures can check pass/fail."""
    outcome = yield
    rep = outcome.get_result()
    setattr(item, f"rep_{rep.when}", rep)


# ---------------------------------------------------------------------------
# Session fixtures
# ---------------------------------------------------------------------------

@pytest.fixture(scope="session", autouse=True)
def _check_app_running():
    """Skip entire session if the app is unreachable."""
    try:
        urllib.request.urlopen(f"{APP_URL}/Identity/Account/Login", timeout=5)
    except Exception:
        pytest.skip(f"App not running at {APP_URL}. Start it first.")


@pytest.fixture(scope="session")
def browser():
    with sync_playwright() as pw:
        b = pw.chromium.launch(headless=True)
        yield b
        b.close()


@pytest.fixture(scope="session")
def auth_storage(browser):
    """Log in once (eagerly) and capture the storage state."""
    ctx = browser.new_context()
    page = ctx.new_page()
    page.goto(f"{APP_URL}/Identity/Account/Login", wait_until="domcontentloaded")
    page.wait_for_selector("#Input_Email", timeout=15_000)
    page.fill("#Input_Email", ADMIN_EMAIL)
    page.fill("#Input_Password", ADMIN_PASSWORD)
    page.click("button[type='submit']")
    page.wait_for_load_state("networkidle", timeout=15_000)
    assert "Login" not in page.url, f"Login failed — still on {page.url}"
    state = ctx.storage_state()
    page.close()
    ctx.close()
    return state


@pytest.fixture(scope="session", autouse=True)
def _init_auth(auth_storage):
    """Force auth_storage to run at session start, before any rate-limited login tests."""
    return auth_storage


# ---------------------------------------------------------------------------
# Per-test fixtures
# ---------------------------------------------------------------------------

@pytest.fixture()
def page(browser, request):
    """Unauthenticated page — for login / access-denied tests."""
    ctx = browser.new_context()
    p = ctx.new_page()
    yield p
    _screenshot_on_failure(p, request)
    p.close()
    ctx.close()


@pytest.fixture()
def auth_page(browser, auth_storage, request):
    """Authenticated page (admin user) with JS-error tracking."""
    ctx = browser.new_context(storage_state=auth_storage)
    p = ctx.new_page()

    # Collect JS errors so tests can assert zero errors
    js_errors: list[str] = []
    p.on("pageerror", lambda err: js_errors.append(str(err)))
    p._js_errors = js_errors  # type: ignore[attr-defined]

    yield p
    _screenshot_on_failure(p, request)
    p.close()
    ctx.close()


@pytest.fixture()
def mobile_page(browser, auth_storage, request):
    """Authenticated page emulating an iPhone 12 viewport."""
    ctx = browser.new_context(
        storage_state=auth_storage,
        viewport={"width": 390, "height": 844},
        user_agent=(
            "Mozilla/5.0 (iPhone; CPU iPhone OS 15_0 like Mac OS X) "
            "AppleWebKit/605.1.15 (KHTML, like Gecko) Version/15.0 Mobile/15E148 Safari/604.1"
        ),
    )
    p = ctx.new_page()
    yield p
    _screenshot_on_failure(p, request)
    p.close()
    ctx.close()


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

def _screenshot_on_failure(page, request):
    """Save a screenshot when a test fails."""
    if hasattr(request.node, "rep_call") and request.node.rep_call.failed:
        os.makedirs(SCREENSHOT_DIR, exist_ok=True)
        path = os.path.join(SCREENSHOT_DIR, f"FAIL_{request.node.name}.png")
        try:
            page.screenshot(path=path, full_page=True)
        except Exception:
            pass  # page may already be closed


def assert_no_errors(page):
    """Assert no server-side error alerts and no JS errors on the current page."""
    # Check for alert-danger divs that actually have text content
    # (the asp-validation-summary div is always present but empty when valid)
    danger_alerts = page.locator(".alert-danger:visible")
    for i in range(danger_alerts.count()):
        text = danger_alerts.nth(i).text_content().strip()
        if text:
            assert False, f"Error alert visible: {text}"
    # No JS errors captured
    js_errors = getattr(page, "_js_errors", [])
    assert len(js_errors) == 0, f"JS errors: {js_errors}"


def assert_success_message(page, timeout=5_000):
    """Assert a success alert appears on the current page (waits up to timeout)."""
    try:
        page.wait_for_selector(".alert-success", state="visible", timeout=timeout)
    except Exception:
        # Check for TempData success in page content as fallback
        content = page.content()
        assert "saved successfully" in content.lower() \
            or "reset to defaults successfully" in content.lower() \
            or "alert-success" in content, \
            f"Expected success message but none found on {page.url}"
