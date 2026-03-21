"""Dashboard, navigation, responsive, and SignalR tests."""

from conftest import APP_URL, assert_no_errors


# ==========================================================================
# Dashboard
# ==========================================================================

class TestDashboard:
    def test_kpi_cards_render(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        for kpi_id in ["#bot-status", "#open-positions", "#total-pnl", "#best-spread"]:
            auth_page.wait_for_selector(kpi_id, timeout=5_000)
            assert auth_page.locator(kpi_id).is_visible()

    def test_opportunities_table_present(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.wait_for_selector("#opportunities-table-body", timeout=5_000)
        assert "Arbitrage Opportunities" in auth_page.content()

    def test_open_positions_section(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        assert "Open Positions" in auth_page.content()

    def test_retry_now_button_visible_for_admin(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.wait_for_selector("#retry-now-btn", timeout=5_000)
        assert auth_page.locator("#retry-now-btn").is_visible()

    def test_best_spread_format(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.wait_for_selector("#best-spread", timeout=5_000)
        text = auth_page.locator("#best-spread").text_content().strip()
        assert text != "", "best-spread is empty"
        assert "%" in text or text == "N/A", f"Unexpected spread value: {text}"

    def test_no_errors_on_dashboard(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.wait_for_selector("#bot-status", timeout=5_000)
        assert_no_errors(auth_page)


# ==========================================================================
# Navigation
# ==========================================================================

class TestNavigation:
    def test_navbar_brand_links_to_dashboard(self, auth_page):
        auth_page.goto(f"{APP_URL}/Alerts")
        auth_page.wait_for_load_state("domcontentloaded")
        auth_page.locator(".navbar-brand").click()
        auth_page.wait_for_load_state("networkidle", timeout=10_000)

    def test_all_main_nav_links_load(self, auth_page):
        """Every main nav target loads without a 500 error."""
        targets = [
            "/Dashboard",
            "/Positions",
            "/Alerts",
            "/Settings/ApiKeys",
            "/Settings/Preferences",
            "/Settings/Configuration",
        ]
        for path in targets:
            auth_page.goto(f"{APP_URL}{path}")
            auth_page.wait_for_load_state("domcontentloaded")
            assert "An error occurred" not in auth_page.content(), f"Error on {path}"
            assert auth_page.locator(".navbar").count() > 0, f"No navbar on {path}"

    def test_admin_dropdown_items(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.locator(".nav-item.dropdown > a.dropdown-toggle").click()
        auth_page.wait_for_selector(".dropdown-menu.show", timeout=3_000)
        menu = auth_page.locator(".dropdown-menu.show").text_content()
        for item in ["Overview", "Bot Configuration", "Exchanges", "Assets", "User Management"]:
            assert item in menu, f"'{item}' not in admin dropdown"

    def test_all_admin_pages_load(self, auth_page):
        """Every admin page loads without error."""
        targets = [
            "/Admin/Overview",
            "/Admin/BotConfig",
            "/Admin/Exchange",
            "/Admin/Asset",
            "/Admin/Users",
        ]
        for path in targets:
            auth_page.goto(f"{APP_URL}{path}")
            auth_page.wait_for_load_state("domcontentloaded")
            assert "An error occurred" not in auth_page.content(), f"Error on {path}"

    def test_settings_subnav_tabs(self, auth_page):
        auth_page.goto(f"{APP_URL}/Settings/ApiKeys")
        content = auth_page.content()
        # Settings page should have sub-navigation to the three settings sections
        assert "API Keys" in content
        assert "Preferences" in content or "Exchanges" in content
        assert "Configuration" in content or "Bot Configuration" in content

    def test_footer_present(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        assert auth_page.locator("footer").count() > 0


# ==========================================================================
# SignalR
# ==========================================================================

class TestSignalR:
    def test_connection_status_badge_exists(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.wait_for_selector("#connection-status", timeout=5_000)
        text = auth_page.locator("#connection-status").text_content().strip()
        assert text in {"Live", "Connecting...", "Disconnected", "Reconnecting..."}, (
            f"Unexpected status: {text}"
        )

    def test_signalr_scripts_loaded(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        html = auth_page.content().lower()
        assert "signalr" in html, "SignalR script not found"
        assert "dashboard.js" in html or "/hubs/dashboard" in html

    def test_toast_container_present(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.wait_for_selector("#notification-toast-container", timeout=5_000)
        classes = auth_page.locator("#notification-toast-container").get_attribute("class")
        assert "toast-container" in classes


# ==========================================================================
# Responsive / Mobile
# ==========================================================================

class TestResponsive:
    def test_mobile_dashboard_no_overflow(self, mobile_page):
        mobile_page.goto(f"{APP_URL}/Dashboard")
        mobile_page.wait_for_selector("#bot-status", timeout=5_000)
        scroll_w = mobile_page.evaluate("document.body.scrollWidth")
        assert scroll_w <= 400, f"Horizontal overflow: scrollWidth={scroll_w}"

    def test_mobile_hamburger_menu(self, mobile_page):
        mobile_page.goto(f"{APP_URL}/Dashboard")
        mobile_page.wait_for_selector(".navbar-toggler", timeout=5_000)
        mobile_page.locator(".navbar-toggler").click()
        mobile_page.wait_for_selector(".navbar-collapse.show", timeout=3_000)

    def test_mobile_kpi_cards_visible(self, mobile_page):
        mobile_page.goto(f"{APP_URL}/Dashboard")
        mobile_page.wait_for_selector("#bot-status", timeout=5_000)
        for kpi_id in ["#bot-status", "#open-positions", "#total-pnl", "#best-spread"]:
            assert mobile_page.locator(kpi_id).count() > 0, f"{kpi_id} not found on mobile"

    def test_mobile_config_form_no_overflow(self, mobile_page):
        mobile_page.goto(f"{APP_URL}/Settings/Configuration")
        mobile_page.wait_for_load_state("domcontentloaded")
        scroll_w = mobile_page.evaluate("document.body.scrollWidth")
        assert scroll_w <= 400, f"Horizontal overflow on config: scrollWidth={scroll_w}"
