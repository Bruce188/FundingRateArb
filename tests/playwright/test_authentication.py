"""Authentication and authorization tests."""

from conftest import APP_URL, ADMIN_EMAIL, ADMIN_PASSWORD


class TestLogin:
    def test_valid_login_redirects_to_dashboard(self, page):
        page.goto(f"{APP_URL}/Identity/Account/Login")
        page.wait_for_load_state("domcontentloaded")
        page.fill("#Input_Email", ADMIN_EMAIL)
        page.fill("#Input_Password", ADMIN_PASSWORD)
        page.click("button[type='submit']")
        page.wait_for_load_state("networkidle", timeout=15_000)
        # After login, app redirects to "/" (Dashboard is default controller)
        assert "Login" not in page.url, f"Still on login page: {page.url}"

    def test_invalid_password_shows_error(self, page):
        page.goto(f"{APP_URL}/Identity/Account/Login", wait_until="domcontentloaded")
        page.wait_for_selector("#Input_Email", timeout=15_000)
        page.fill("#Input_Email", ADMIN_EMAIL)
        page.fill("#Input_Password", "WrongPassword!999")
        page.click("button[type='submit']")
        page.wait_for_load_state("domcontentloaded")
        assert "Invalid login attempt" in page.content()

    def test_empty_email_prevents_submit(self, page):
        page.goto(f"{APP_URL}/Identity/Account/Login")
        page.wait_for_load_state("domcontentloaded")
        page.fill("#Input_Password", ADMIN_PASSWORD)
        page.click("button[type='submit']")
        page.wait_for_load_state("domcontentloaded")
        # Should stay on login page (HTML5 required validation)
        assert "Login" in page.url or "Account" in page.url

    def test_empty_password_prevents_submit(self, page):
        page.goto(f"{APP_URL}/Identity/Account/Login")
        page.wait_for_load_state("domcontentloaded")
        page.fill("#Input_Email", ADMIN_EMAIL)
        page.click("button[type='submit']")
        page.wait_for_load_state("domcontentloaded")
        assert "Login" in page.url or "Account" in page.url


class TestLogout:
    def test_logout_redirects_away_from_dashboard(self, auth_page):
        auth_page.goto(f"{APP_URL}/Dashboard")
        auth_page.wait_for_load_state("domcontentloaded")
        # Click the logout button (POST form in navbar)
        auth_page.locator("form[action*='Logout'] button[type='submit']").click()
        auth_page.wait_for_load_state("networkidle", timeout=10_000)
        # Should no longer see dashboard KPI elements
        assert "bot-status" not in auth_page.content()


class TestAccessControl:
    def test_unauthenticated_dashboard_redirects_to_login(self, page):
        page.goto(f"{APP_URL}/Dashboard")
        page.wait_for_load_state("networkidle", timeout=10_000)
        assert "Login" in page.url or "Account" in page.url

    def test_unauthenticated_admin_redirects_to_login(self, page):
        page.goto(f"{APP_URL}/Admin/BotConfig")
        page.wait_for_load_state("networkidle", timeout=10_000)
        assert "Login" in page.url or "Account" in page.url

    def test_unauthenticated_settings_redirects_to_login(self, page):
        page.goto(f"{APP_URL}/Settings/Configuration")
        page.wait_for_load_state("networkidle", timeout=10_000)
        assert "Login" in page.url or "Account" in page.url
