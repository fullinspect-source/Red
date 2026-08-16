import pathlib
import unittest

ROOT = pathlib.Path(__file__).resolve().parents[1]
ENERGY = (ROOT / "Services" / "EnergyComplianceService.cs").read_text(encoding="utf-8")
MAIN = (ROOT / "MainWindow.xaml.cs").read_text(encoding="utf-8")
LOOKUP = (ROOT / "Services" / "EquipmentAirflowService.cs").read_text(encoding="utf-8")


class EquipmentAirflowUiStaticTests(unittest.TestCase):
    def test_image_only_rotated_ec_reports_retry_ocr_orientation(self):
        self.assertIn("RotateFlipType.Rotate90FlipNone", ENERGY)
        self.assertIn("RotateFlipType.Rotate180FlipNone", ENERGY)
        self.assertIn("RotateFlipType.Rotate270FlipNone", ENERGY)
        self.assertIn("if (bestText.Trim().Length >= 50)", ENERGY)

    def test_all_approved_matchups_are_present(self):
        for outdoor, indoor, cfm in (
            ("GZV6SA24", "AHVE24BP13", "773"),
            ("GZV6SA30", "AHVE36CP13", "947"),
            ("GZV6SA36", "AHVE36CP13", "1140"),
            ("GLZS4BA30", "AMST42CU13", "953"),
            ("GZV6SA42", "AHVE42CP13", "1367"),
        ):
            self.assertIn(f'new("{outdoor}", "{indoor}", {cfm})', LOOKUP)

    def test_same_job_energy_final_lookup_is_wired_into_background_load(self):
        self.assertIn("FindSameJobEnergyFinalFiles", LOOKUP)
        self.assertIn("EquipmentAirflowService.ApplyMatches(info, capturedFilePath, null)", MAIN)

    def test_energy_final_guidance_is_display_only(self):
        self.assertIn("EquipmentAirflowDisplayMappings", ENERGY)
        self.assertIn("CanApplyToItem", ENERGY)
        self.assertIn("ApplyEcButton.Visibility = canApply ? Visibility.Visible : Visibility.Collapsed", MAIN)

    def test_system_two_uses_its_own_target(self):
        self.assertIn('{ ("AFI", "2.5"),   i => i.DesignAirflowCfm2 ?? i.DesignAirflowCfm }', ENERGY)
        self.assertIn('{ ("AFI", "3.9"),   i => i.DesignAirflowCfm2 ?? i.DesignAirflowCfm }', ENERGY)


if __name__ == "__main__":
    unittest.main()
