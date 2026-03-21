"""Admin panel tests: Bot Config, Exchanges, Assets, Users, Overview."""

from conftest import APP_URL, assert_no_errors, assert_success_message


# ==========================================================================
# Admin Overview
# ==========================================================================

class TestAdminOverview:
    def test_overview_loads_with_kpi_cards(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Overview")
        auth_page.wait_for_load_state("domcontentloaded")
        assert auth_page.locator(".card").count() >= 3, "Expected KPI cards"
        # Note: Overview page may show a "STOPPED" status alert using alert-danger
        # styling — this is a legitimate status indicator, not an error.
        assert "An error occurred" not in auth_page.content()

    def test_overview_has_user_table(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Overview")
        auth_page.wait_for_load_state("domcontentloaded")
        assert auth_page.locator("table").count() > 0, "User activity table not found"


# ==========================================================================
# Admin Bot Config
# ==========================================================================

ADMIN_CONFIG_FIELDS = {
    "OpenThreshold": "0.004",
    "CloseThreshold": "0.001",
    "AlertThreshold": "0.002",
    "TotalCapitalUsdc": "500",
    "DefaultLeverage": "3",
    "MaxCapitalPerPosition": "0.25",
    "MaxConcurrentPositions": "2",
    "StopLossPct": "0.15",
    "MaxHoldTimeHours": "72",
    "VolumeFraction": "0.005",
    "BreakevenHoursMax": "48",
    "FeeAmortizationHours": "12",
    "MinPositionSizeUsdc": "5",
    "MinVolume24hUsdc": "100000",
    "RateStalenessMinutes": "15",
    "DailyDrawdownPausePct": "0.05",
    "ConsecutiveLossPause": "3",
    "AllocationTopN": "3",
}


class TestAdminBotConfig:
    def test_page_loads_with_all_fields(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")
        for field_id in ADMIN_CONFIG_FIELDS:
            assert auth_page.locator(f"#{field_id}").count() > 0, f"#{field_id} not found"
        assert_no_errors(auth_page)

    def test_kill_switch_button_visible(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")
        content = auth_page.content()
        assert "Kill Switch" in content or "Enable Bot" in content

    def test_status_badge_visible(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")
        content = auth_page.content()
        assert "RUNNING" in content or "STOPPED" in content

    def test_save_admin_config_success(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")

        for field_id, value in ADMIN_CONFIG_FIELDS.items():
            auth_page.fill(f"#{field_id}", value)

        auth_page.click("button[type='submit']:has-text('Save')")
        auth_page.wait_for_load_state("domcontentloaded")
        assert_success_message(auth_page)
        assert_no_errors(auth_page)

    def test_admin_config_section_headers(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        content = auth_page.content()
        for heading in ["Thresholds", "Capital", "Risk Management", "Advanced"]:
            assert heading in content, f"Section '{heading}' not in admin config"

    def test_kill_switch_toggle(self, auth_page):
        """Toggle the kill switch and verify the status changes."""
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")

        # Determine current state
        was_running = "RUNNING" in auth_page.content()

        # Click the kill switch / enable button
        if was_running:
            auth_page.click("button:has-text('Kill Switch')")
        else:
            auth_page.click("button:has-text('Enable Bot')")

        auth_page.wait_for_load_state("domcontentloaded")

        # State should have changed
        if was_running:
            assert "STOPPED" in auth_page.content()
        else:
            assert "RUNNING" in auth_page.content()

        # Toggle back to restore original state
        if was_running:
            auth_page.click("button:has-text('Enable Bot')")
        else:
            auth_page.click("button:has-text('Kill Switch')")
        auth_page.wait_for_load_state("domcontentloaded")


# ==========================================================================
# Admin Exchanges CRUD
# ==========================================================================

class TestAdminExchanges:
    def test_exchange_list_loads(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Exchange")
        auth_page.wait_for_load_state("domcontentloaded")
        assert auth_page.locator("table").count() > 0, "Exchange table not found"
        assert "Exchanges" in auth_page.content()
        assert_no_errors(auth_page)

    def test_exchange_table_has_rows(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Exchange")
        auth_page.wait_for_load_state("domcontentloaded")
        rows = auth_page.locator("table tbody tr")
        assert rows.count() >= 3, f"Expected >= 3 exchanges, found {rows.count()}"

    def test_exchange_create_page_loads(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Exchange/Create")
        auth_page.wait_for_load_state("domcontentloaded")
        assert auth_page.locator("#Name").count() > 0, "Name field not found"
        assert auth_page.locator("#ApiBaseUrl").count() > 0, "ApiBaseUrl field not found"
        assert_no_errors(auth_page)

    def test_exchange_edit_page_loads(self, auth_page):
        """Navigate to the edit page for the first exchange."""
        auth_page.goto(f"{APP_URL}/Admin/Exchange")
        auth_page.wait_for_load_state("domcontentloaded")
        edit_link = auth_page.locator("a:has-text('Edit')").first
        if edit_link.count() > 0:
            edit_link.click()
            auth_page.wait_for_load_state("domcontentloaded")
            assert auth_page.locator("#Name").count() > 0
            assert_no_errors(auth_page)


# ==========================================================================
# Admin Assets CRUD
# ==========================================================================

class TestAdminAssets:
    def test_asset_list_loads(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Asset")
        auth_page.wait_for_load_state("domcontentloaded")
        assert auth_page.locator("table").count() > 0, "Asset table not found"
        assert "Assets" in auth_page.content()
        assert_no_errors(auth_page)

    def test_asset_table_has_rows(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Asset")
        auth_page.wait_for_load_state("domcontentloaded")
        rows = auth_page.locator("table tbody tr")
        assert rows.count() >= 1, f"Expected >= 1 asset, found {rows.count()}"

    def test_asset_create_page_loads(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Asset/Create")
        auth_page.wait_for_load_state("domcontentloaded")
        assert auth_page.locator("#Symbol").count() > 0
        assert auth_page.locator("#Name").count() > 0
        assert_no_errors(auth_page)

    def test_asset_edit_page_loads(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Asset")
        auth_page.wait_for_load_state("domcontentloaded")
        edit_link = auth_page.locator("a:has-text('Edit')").first
        if edit_link.count() > 0:
            edit_link.click()
            auth_page.wait_for_load_state("domcontentloaded")
            assert auth_page.locator("#Symbol").count() > 0
            assert_no_errors(auth_page)


# ==========================================================================
# Admin Users
# ==========================================================================

class TestAdminUsers:
    def test_users_list_loads(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Users")
        auth_page.wait_for_load_state("domcontentloaded")
        assert auth_page.locator("table").count() > 0, "Users table not found"
        assert_no_errors(auth_page)

    def test_users_table_shows_admin(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Users")
        auth_page.wait_for_load_state("domcontentloaded")
        content = auth_page.content()
        assert "admin@fundingratearb.com" in content, "Admin user not in users list"

    def test_edit_role_page_loads(self, auth_page):
        auth_page.goto(f"{APP_URL}/Admin/Users")
        auth_page.wait_for_load_state("domcontentloaded")
        edit_btn = auth_page.locator("a:has-text('Edit Role')").first
        if edit_btn.count() > 0:
            edit_btn.click()
            auth_page.wait_for_load_state("domcontentloaded")
            content = auth_page.content()
            assert "Admin" in content or "Trader" in content
            assert_no_errors(auth_page)
