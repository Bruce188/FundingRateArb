"""Stress tests: rapid configuration changes without errors.

These tests verify that quickly modifying and saving settings does not
produce server errors, validation errors, or JS exceptions — even when
submitted multiple times in quick succession.
"""

from conftest import APP_URL, assert_no_errors, assert_success_message


# ==========================================================================
# User Configuration — Rapid Changes
# ==========================================================================

class TestRapidUserConfig:
    def test_rapid_save_10_times(self, auth_page):
        """Change a field and save 10 times in quick succession — no errors."""
        auth_page.goto(f"{APP_URL}/Settings/Configuration")
        auth_page.wait_for_load_state("domcontentloaded")

        for i in range(10):
            capital = str(100 + i * 10)  # 100, 110, 120 ... 190
            auth_page.fill("#TotalCapitalUsdc", capital)
            auth_page.fill("#DefaultLeverage", str((i % 10) + 1))  # 1-10
            auth_page.click("button[type='submit']:has-text('Save')")
            auth_page.wait_for_load_state("domcontentloaded")

            assert_success_message(auth_page)
            assert_no_errors(auth_page)

    def test_rapid_all_fields_change_and_save(self, auth_page):
        """Modify every single field, save, and repeat 5 times."""
        auth_page.goto(f"{APP_URL}/Settings/Configuration")
        auth_page.wait_for_load_state("domcontentloaded")

        field_variants = [
            {  # Variant 0
                "OpenThreshold": "0.002", "CloseThreshold": "0.001",
                "AlertThreshold": "0.003", "TotalCapitalUsdc": "100",
                "DefaultLeverage": "3", "MaxCapitalPerPosition": "0.25",
                "MaxConcurrentPositions": "2", "StopLossPct": "0.05",
                "DailyDrawdownPausePct": "0.05", "ConsecutiveLossPause": "2",
                "FeeAmortizationHours": "12", "MaxHoldTimeHours": "24",
                "MinPositionSizeUsdc": "5", "MinVolume24hUsdc": "10000",
                "RateStalenessMinutes": "5", "AllocationTopN": "3",
            },
            {  # Variant 1
                "OpenThreshold": "0.01", "CloseThreshold": "0.005",
                "AlertThreshold": "0.008", "TotalCapitalUsdc": "500",
                "DefaultLeverage": "10", "MaxCapitalPerPosition": "0.5",
                "MaxConcurrentPositions": "5", "StopLossPct": "0.1",
                "DailyDrawdownPausePct": "0.1", "ConsecutiveLossPause": "5",
                "FeeAmortizationHours": "48", "MaxHoldTimeHours": "168",
                "MinPositionSizeUsdc": "20", "MinVolume24hUsdc": "50000",
                "RateStalenessMinutes": "15", "AllocationTopN": "5",
            },
            {  # Variant 2
                "OpenThreshold": "0.05", "CloseThreshold": "0.01",
                "AlertThreshold": "0.02", "TotalCapitalUsdc": "1000",
                "DefaultLeverage": "20", "MaxCapitalPerPosition": "0.75",
                "MaxConcurrentPositions": "10", "StopLossPct": "0.2",
                "DailyDrawdownPausePct": "0.15", "ConsecutiveLossPause": "8",
                "FeeAmortizationHours": "72", "MaxHoldTimeHours": "360",
                "MinPositionSizeUsdc": "50", "MinVolume24hUsdc": "200000",
                "RateStalenessMinutes": "30", "AllocationTopN": "10",
            },
            {  # Variant 3 — near-minimum values
                "OpenThreshold": "0.00001", "CloseThreshold": "0.00001",
                "AlertThreshold": "0.00001", "TotalCapitalUsdc": "1",
                "DefaultLeverage": "1", "MaxCapitalPerPosition": "0.01",
                "MaxConcurrentPositions": "1", "StopLossPct": "0.001",
                "DailyDrawdownPausePct": "0.01", "ConsecutiveLossPause": "1",
                "FeeAmortizationHours": "1", "MaxHoldTimeHours": "1",
                "MinPositionSizeUsdc": "1", "MinVolume24hUsdc": "0",
                "RateStalenessMinutes": "1", "AllocationTopN": "1",
            },
            {  # Variant 4 — near-maximum values
                "OpenThreshold": "0.5", "CloseThreshold": "0.5",
                "AlertThreshold": "0.5", "TotalCapitalUsdc": "100000",
                "DefaultLeverage": "50", "MaxCapitalPerPosition": "1",
                "MaxConcurrentPositions": "20", "StopLossPct": "1",
                "DailyDrawdownPausePct": "1", "ConsecutiveLossPause": "20",
                "FeeAmortizationHours": "168", "MaxHoldTimeHours": "720",
                "MinPositionSizeUsdc": "10000", "MinVolume24hUsdc": "10000000",
                "RateStalenessMinutes": "60", "AllocationTopN": "20",
            },
        ]

        for i, variant in enumerate(field_variants):
            for field_id, value in variant.items():
                auth_page.fill(f"#{field_id}", value)

            auth_page.click("button[type='submit']:has-text('Save')")
            auth_page.wait_for_load_state("domcontentloaded")

            assert_success_message(auth_page), f"No success on variant {i}"
            assert_no_errors(auth_page)

    def test_rapid_field_tabbing_no_js_errors(self, auth_page):
        """Rapidly tab through all fields, changing values — no JS errors."""
        auth_page.goto(f"{APP_URL}/Settings/Configuration")
        auth_page.wait_for_load_state("domcontentloaded")

        fields = [
            "OpenThreshold", "CloseThreshold", "AlertThreshold",
            "TotalCapitalUsdc", "DefaultLeverage", "MaxCapitalPerPosition",
            "MaxConcurrentPositions", "StopLossPct", "DailyDrawdownPausePct",
            "ConsecutiveLossPause", "FeeAmortizationHours", "MaxHoldTimeHours",
            "MinPositionSizeUsdc", "MinVolume24hUsdc", "RateStalenessMinutes",
            "AllocationTopN",
        ]
        values = [
            "0.003", "0.001", "0.002",
            "250", "5", "0.3",
            "3", "0.08", "0.06",
            "4", "24", "48",
            "10", "25000", "10",
            "4",
        ]

        # Rapidly fill each field without pausing
        for field_id, value in zip(fields, values):
            auth_page.locator(f"#{field_id}").click()
            auth_page.fill(f"#{field_id}", value)

        # No JS errors should have occurred during rapid input
        js_errors = getattr(auth_page, "_js_errors", [])
        assert len(js_errors) == 0, f"JS errors during rapid field changes: {js_errors}"

        # Now save
        auth_page.click("button[type='submit']:has-text('Save')")
        auth_page.wait_for_load_state("domcontentloaded")
        assert_success_message(auth_page)
        assert_no_errors(auth_page)

    def test_toggle_bot_on_off_rapidly(self, auth_page):
        """Toggle the personal bot checkbox multiple times, save each time."""
        auth_page.goto(f"{APP_URL}/Settings/Configuration")
        auth_page.wait_for_load_state("domcontentloaded")

        checkbox = auth_page.locator("#IsEnabled, #isEnabledSwitch")
        if checkbox.count() == 0:
            return  # Skip if checkbox not found

        for _ in range(6):
            if checkbox.is_checked():
                checkbox.uncheck()
            else:
                checkbox.check()
            auth_page.click("button[type='submit']:has-text('Save')")
            auth_page.wait_for_load_state("domcontentloaded")
            assert_no_errors(auth_page)

    def test_allocation_strategy_change_rapid(self, auth_page):
        """Cycle through all allocation strategies, saving each time."""
        auth_page.goto(f"{APP_URL}/Settings/Configuration")
        auth_page.wait_for_load_state("domcontentloaded")

        select = auth_page.locator("#AllocationStrategy")
        options = auth_page.locator("#AllocationStrategy option")
        option_count = options.count()

        for i in range(option_count):
            value = options.nth(i).get_attribute("value")
            if value is not None:
                select.select_option(value=value)
                auth_page.click("button[type='submit']:has-text('Save')")
                auth_page.wait_for_load_state("domcontentloaded")
                assert_success_message(auth_page)
                assert_no_errors(auth_page)


# ==========================================================================
# Admin Bot Config — Rapid Changes
# ==========================================================================

class TestRapidAdminConfig:
    def test_rapid_admin_save_10_times(self, auth_page):
        """Save admin config 10 times in quick succession — no errors."""
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")

        # Ensure required fields have valid values before rapid changes
        auth_page.fill("#VolumeFraction", "0.005")
        auth_page.fill("#BreakevenHoursMax", "48")
        # Set thresholds that satisfy OpenThreshold > AlertThreshold > CloseThreshold
        auth_page.fill("#CloseThreshold", "0.0001")
        auth_page.fill("#AlertThreshold", "0.0005")

        for i in range(10):
            auth_page.fill("#TotalCapitalUsdc", str(200 + i * 50))
            auth_page.fill("#DefaultLeverage", str((i % 10) + 1))
            auth_page.fill("#OpenThreshold", f"0.00{i + 1}")
            auth_page.click("button[type='submit']:has-text('Save')")
            auth_page.wait_for_load_state("domcontentloaded")

            assert_success_message(auth_page)
            assert_no_errors(auth_page)

    def test_rapid_admin_all_fields_change(self, auth_page):
        """Change every admin config field and save 3 times."""
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")

        variants = [
            {
                "OpenThreshold": "0.003", "CloseThreshold": "0.001",
                "AlertThreshold": "0.002", "TotalCapitalUsdc": "300",
                "DefaultLeverage": "5", "MaxCapitalPerPosition": "0.3",
                "MaxConcurrentPositions": "3", "StopLossPct": "0.1",
                "MaxHoldTimeHours": "48", "VolumeFraction": "0.003",
                "BreakevenHoursMax": "24", "FeeAmortizationHours": "24",
                "MinPositionSizeUsdc": "10", "MinVolume24hUsdc": "50000",
                "RateStalenessMinutes": "10", "DailyDrawdownPausePct": "0.05",
                "ConsecutiveLossPause": "3", "AllocationTopN": "3",
            },
            {
                "OpenThreshold": "0.01", "CloseThreshold": "0.005",
                "AlertThreshold": "0.007", "TotalCapitalUsdc": "1000",
                "DefaultLeverage": "10", "MaxCapitalPerPosition": "0.5",
                "MaxConcurrentPositions": "5", "StopLossPct": "0.15",
                "MaxHoldTimeHours": "120", "VolumeFraction": "0.01",
                "BreakevenHoursMax": "72", "FeeAmortizationHours": "48",
                "MinPositionSizeUsdc": "25", "MinVolume24hUsdc": "100000",
                "RateStalenessMinutes": "20", "DailyDrawdownPausePct": "0.1",
                "ConsecutiveLossPause": "5", "AllocationTopN": "5",
            },
            {
                "OpenThreshold": "0.005", "CloseThreshold": "0.002",
                "AlertThreshold": "0.004", "TotalCapitalUsdc": "500",
                "DefaultLeverage": "7", "MaxCapitalPerPosition": "0.4",
                "MaxConcurrentPositions": "4", "StopLossPct": "0.08",
                "MaxHoldTimeHours": "72", "VolumeFraction": "0.005",
                "BreakevenHoursMax": "48", "FeeAmortizationHours": "36",
                "MinPositionSizeUsdc": "15", "MinVolume24hUsdc": "75000",
                "RateStalenessMinutes": "15", "DailyDrawdownPausePct": "0.07",
                "ConsecutiveLossPause": "4", "AllocationTopN": "4",
            },
        ]

        for i, variant in enumerate(variants):
            for field_id, value in variant.items():
                auth_page.fill(f"#{field_id}", value)

            auth_page.click("button[type='submit']:has-text('Save')")
            auth_page.wait_for_load_state("domcontentloaded")

            assert_success_message(auth_page), f"No success on admin variant {i}"
            assert_no_errors(auth_page)

    def test_kill_switch_toggle_rapid(self, auth_page):
        """Toggle kill switch 6 times rapidly — no errors."""
        auth_page.goto(f"{APP_URL}/Admin/BotConfig")
        auth_page.wait_for_load_state("domcontentloaded")

        for _ in range(6):
            content = auth_page.content()
            if "Kill Switch" in content:
                auth_page.locator("button:has-text('Kill Switch')").click()
            elif "Enable Bot" in content:
                auth_page.locator("button:has-text('Enable Bot')").click()
            else:
                break
            auth_page.wait_for_load_state("domcontentloaded")
            assert_no_errors(auth_page)


# ==========================================================================
# Preferences — Rapid Changes
# ==========================================================================

class TestRapidPreferences:
    def test_rapid_toggle_assets_and_save(self, auth_page):
        """Toggle assets on/off rapidly, save each time."""
        auth_page.goto(f"{APP_URL}/Settings/Preferences")
        auth_page.wait_for_load_state("domcontentloaded")

        for round_num in range(5):
            # Set exchanges (≥2 required) and assets via JS each round
            if round_num % 2 == 0:
                # Enable all assets
                auth_page.evaluate("""
                    const exchanges = document.querySelectorAll('.exchange-toggle');
                    exchanges.forEach((cb, i) => { cb.checked = (i < 3); });
                    document.querySelectorAll('.asset-toggle').forEach(cb => cb.checked = true);
                    const ew = document.getElementById('exchange-validation-warning');
                    if (ew) ew.classList.add('d-none');
                    const aw = document.getElementById('asset-validation-warning');
                    if (aw) aw.classList.add('d-none');
                """)
            else:
                # Enable only the first asset (minimum 1 required)
                auth_page.evaluate("""
                    const exchanges = document.querySelectorAll('.exchange-toggle');
                    exchanges.forEach((cb, i) => { cb.checked = (i < 3); });
                    document.querySelectorAll('.asset-toggle').forEach(cb => cb.checked = false);
                    const first = document.querySelector('.asset-toggle');
                    if (first) first.checked = true;
                    const ew = document.getElementById('exchange-validation-warning');
                    if (ew) ew.classList.add('d-none');
                    const aw = document.getElementById('asset-validation-warning');
                    if (aw) aw.classList.add('d-none');
                """)

            # Submit directly to avoid client-side validation timing issues
            auth_page.evaluate("document.getElementById('preferencesForm').submit()")
            auth_page.wait_for_load_state("domcontentloaded")
            assert_success_message(auth_page)
            assert_no_errors(auth_page)


# ==========================================================================
# Cross-Page Rapid Navigation
# ==========================================================================

class TestRapidNavigation:
    def test_rapid_page_navigation_no_errors(self, auth_page):
        """Navigate through all major pages rapidly — no errors on any page."""
        pages = [
            "/Dashboard",
            "/Settings/Configuration",
            "/Settings/Preferences",
            "/Settings/ApiKeys",
            "/Positions",
            "/Alerts",
            "/Admin/Overview",
            "/Admin/BotConfig",
            "/Admin/Exchange",
            "/Admin/Asset",
            "/Admin/Users",
        ]

        for _ in range(3):  # 3 full cycles
            for path in pages:
                auth_page.goto(f"{APP_URL}{path}")
                auth_page.wait_for_load_state("domcontentloaded")
                # Should never see a 500 error page
                content = auth_page.content()
                assert "An error occurred" not in content, f"Error on {path}"
                assert "500" not in auth_page.title(), f"500 error on {path}"

        # No JS errors accumulated across all navigation
        js_errors = getattr(auth_page, "_js_errors", [])
        assert len(js_errors) == 0, f"JS errors during rapid navigation: {js_errors}"
