# This Source Code Form is subject to the terms of the Mozilla Public
# License, v. 2.0. If a copy of the MPL was not distributed with this
# file, You can obtain one at https://mozilla.org/MPL/2.0/.

"""Tests for the repository checks: each check must fail on a faulty case."""

import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent.parent))

import check_headers  # noqa: E402
import check_links  # noqa: E402
import check_pr  # noqa: E402
import check_terms  # noqa: E402

HEADER_SLASH = (
    "// This Source Code Form is subject to the terms of the Mozilla Public\n"
    "// License, v. 2.0. If a copy of the MPL was not distributed with this\n"
    "// file, You can obtain one at https://mozilla.org/MPL/2.0/.\n"
)
HEADER_XML = (
    "<!--\n"
    "  This Source Code Form is subject to the terms of the Mozilla Public\n"
    "  License, v. 2.0. If a copy of the MPL was not distributed with this\n"
    "  file, You can obtain one at https://mozilla.org/MPL/2.0/.\n"
    "-->\n"
)


class TempRepo(unittest.TestCase):
    """Base class: a scratch directory used as the repository root."""

    def setUp(self):
        self._tmp = tempfile.TemporaryDirectory()
        self.root = Path(self._tmp.name)

    def tearDown(self):
        self._tmp.cleanup()

    def write(self, name: str, text: str) -> Path:
        path = self.root / name
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(text, encoding="utf-8", newline="\n")
        return path


class HeaderTests(TempRepo):
    def test_accepts_slash_comment_header(self):
        self.write("src/A.cs", HEADER_SLASH + "class A {}\n")
        self.assertEqual(check_headers.check(self.root), [])

    def test_accepts_xml_comment_header_after_declaration(self):
        self.write("Directory.Build.props", '<?xml version="1.0"?>\n' + HEADER_XML + "<Project />\n")
        self.assertEqual(check_headers.check(self.root), [])

    def test_rejects_missing_header(self):
        self.write("src/A.cs", "class A {}\n")
        self.assertEqual(len(check_headers.check(self.root)), 1)

    def test_rejects_altered_header(self):
        self.write("src/A.cs", HEADER_SLASH.replace("2.0", "1.1") + "class A {}\n")
        self.assertEqual(len(check_headers.check(self.root)), 1)

    def test_rejects_header_far_from_the_top(self):
        self.write("src/A.cs", "\n" * 30 + HEADER_SLASH)
        self.assertEqual(len(check_headers.check(self.root)), 1)

    def test_covers_every_source_extension(self):
        for ext in (".proto", ".props", ".targets", ".py", ".sh", ".ps1"):
            self.write(f"x/file{ext}", "content\n")
        self.assertEqual(len(check_headers.check(self.root)), 6)

    def test_ignores_other_files_and_build_output(self):
        self.write("README.md", "text\n")
        self.write("src/obj/Generated.cs", "class G {}\n")
        self.assertEqual(check_headers.check(self.root), [])


class TermsTests(TempRepo):
    def setUp(self):
        super().setUp()
        self.terms = check_terms.load_terms()
        self.assertTrue(self.terms)

    def test_detects_each_term_ignoring_case(self):
        for term in self.terms:
            with self.subTest(term=term):
                self.assertTrue(check_terms.check_text(f"see {term.upper()} here", self.terms))
                self.assertTrue(check_terms.check_text(f"see {term.lower()} here", self.terms))

    def test_accepts_clean_text(self):
        self.assertEqual(check_terms.check_text("A voxel engine.\n", self.terms), [])

    def test_repository_scan_reports_file_and_line(self):
        self.write("docs/a.md", f"clean\nmentions {self.terms[0]}\n")
        errors = check_terms.check_repository(self.root, terms=self.terms)
        self.assertEqual(len(errors), 1)
        self.assertIn("docs/a.md:2", errors[0])

    def test_repository_scan_checks_file_names(self):
        self.write(f"docs/{self.terms[0]}.md", "clean\n")
        self.assertEqual(len(check_terms.check_repository(self.root, terms=self.terms)), 1)

    def test_terms_file_is_excluded(self):
        config = self.write("config/terms.txt", "\n".join(self.terms))
        errors = check_terms.check_repository(self.root, terms_file=config, terms=self.terms)
        self.assertEqual(errors, [])


class LinkTests(TempRepo):
    def test_accepts_valid_links_and_anchors(self):
        self.write("docs/a.md", "# A\n\n[b](b.md) [section](b.md#second-part) [top](#a)\n")
        self.write("docs/b.md", "# B\n\n## Second part\n")
        self.assertEqual(check_links.check(self.root), [])

    def test_rejects_missing_file(self):
        self.write("docs/a.md", "# A\n\n[b](missing.md)\n")
        self.assertEqual(len(check_links.check(self.root)), 1)

    def test_rejects_unknown_anchor(self):
        self.write("docs/a.md", "# A\n\n[x](#nowhere)\n")
        self.assertEqual(len(check_links.check(self.root)), 1)

    def test_rejects_unknown_anchor_in_other_file(self):
        self.write("docs/a.md", "# A\n\n[x](b.md#nowhere)\n")
        self.write("docs/b.md", "# B\n")
        self.assertEqual(len(check_links.check(self.root)), 1)

    def test_ignores_external_links_and_code(self):
        self.write(
            "docs/a.md",
            "# A\n\n[web](https://example.com/x) [mail](mailto:a@b.c)\n"
            "`[no](nothing.md)`\n\n```\n[no](nothing.md)\n```\n",
        )
        self.assertEqual(check_links.check(self.root), [])

    def test_rejects_broken_reference_definition(self):
        self.write("docs/a.md", "# A\n\n[ref]: missing.md\n")
        self.assertEqual(len(check_links.check(self.root)), 1)


class PullRequestTests(TempRepo):
    scopes = {"Core", "nBlocks", "Sdk", "Cli"}

    def check(self, title):
        return check_pr.check_title(title, self.scopes)

    def test_accepts_valid_titles(self):
        for title in (
            "feat(nBlocks): add block states",
            "fix(Core)!: reject cyclic dependencies",
            "chore: bootstrap repository",
            "ci: run link checks",
        ):
            with self.subTest(title=title):
                self.assertEqual(self.check(title), [])

    def test_rejects_unknown_type(self):
        self.assertTrue(self.check("feature: add block states"))

    def test_rejects_unknown_scope(self):
        self.assertTrue(self.check("feat(Nowhere): add block states"))

    def test_rejects_missing_description(self):
        self.assertTrue(self.check("feat(Core):"))
        self.assertTrue(self.check("add block states"))

    def test_rejects_uppercase_initial_and_final_period(self):
        self.assertTrue(self.check("feat(Core): Add block states"))
        self.assertTrue(self.check("feat(Core): add block states."))

    def test_rejects_long_title(self):
        self.assertTrue(self.check("feat(Core): " + "a" * 70))

    def test_scopes_come_from_existing_folders(self):
        (self.root / "src" / "nBlocks").mkdir(parents=True)
        (self.root / "tools" / "Nobara.Sdk").mkdir(parents=True)
        (self.root / "tools" / "Nobara.ResourceCompiler").mkdir(parents=True)
        self.assertEqual(check_pr.known_scopes(self.root), {"nBlocks", "Sdk", "ResourceCompiler"})

    def test_no_scope_is_known_without_folders(self):
        self.assertEqual(check_pr.known_scopes(self.root), set())


if __name__ == "__main__":
    unittest.main()
