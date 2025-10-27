#!/usr/bin/env python3
import os
import re
import sys
import difflib
from pathlib import Path

# Config via env or defaults
MONOLITH = Path(os.getenv("MONOLITH_PATH", "MainLayout.razor"))
STUDENT  = Path(os.getenv("STUDENT_PATH", "StudentLayoutSection.razor"))
COMPANY  = Path(os.getenv("COMPANY_PATH", "CompanyLayoutSection.razor"))
PROF     = Path(os.getenv("PROFESSOR_PATH", "ProfessorLayoutSection.razor"))
RGROUP   = Path(os.getenv("RESEARCH_PATH", "ResearchGroupLayoutSection.razor"))

AUTO_FIX_BRACES = os.getenv("AUTO_FIX_BRACES", "false").lower() in ("1", "true", "yes")

# ---------------- helpers ----------------

def read(p: Path) -> str:
    if not p.exists():
        fail(f"Missing file: {p}")
    return p.read_text(encoding="utf-8", errors="ignore")

def norm_ws(s: str) -> str:
    return re.sub(r"\s+", " ", s).strip()

def strip_region_wrappers(s: str) -> str:
    s = re.sub(r"<!--\s*REGION:\s*\w+\.Start\s*-->", "", s, flags=re.IGNORECASE)
    s = re.sub(r"<!--\s*REGION:\s*\w+\.End\s*-->", "", s, flags=re.IGNORECASE)
    return s

def find_ws_insensitive(hay: str, snippet: str, start=0):
    hay_n  = hay.replace("\r\n", "\n")
    snip_n = snippet.replace("\r\n", "\n")
    esc = re.escape(snip_n)
    pat = re.sub(r"(\\\s)+", r"\\s+", esc)
    m = re.search(pat, hay_n[start:], flags=re.DOTALL)
    return (start + m.start(), start + m.end()) if m else (None, None)

def strip_razor_comments(s: str) -> str:
    s = re.sub(r'@\\*.*?\\*@', '', s, flags=re.S)   # Razor block comments
    s = re.sub(r'/\\*.*?\\*/', '', s, flags=re.S)   # C# block comments
    s = re.sub(r'//.*', '', s)                      # C# line comments
    return s

def brace_balance(block: str) -> tuple[bool, int, int]:
    lines = block.splitlines()
    code_lines = []
    for ln in lines:
        ls = ln.lstrip()
        if (ls.startswith('@')
            or ls.startswith('{')
            or ls.startswith('}')
            or '@if' in ls
            or '@foreach' in ls
            or '@switch' in ls):
            code_lines.append(ln)
    txt = strip_razor_comments("\n".join(code_lines))
    opens = txt.count('{')
    closes = txt.count('}')
    return (opens == closes, opens, closes)

def ensure_starts_with_if_block(name: str, block: str, start_literal: str):
    # Skip BOM
    s = block.lstrip("\ufeff")
    # Skip leading whitespace
    i = 0
    n = len(s)
    while i < n and s[i].isspace():
        i += 1
    # Skip leading REGION or HTML or Razor comments
    # <!-- REGION: ... -->
    while True:
        advanced = False
        if s[i:i+4] == "<!--":
            end = s.find("-->", i)
            if end != -1:
                i = end + 3
                while i < n and s[i].isspace():
                    i += 1
                advanced = True
        # Razor block comment @* ... *@
        if s.startswith("@*", i):
            end = s.find("*@", i)
            if end != -1:
                i = end + 2
                while i < n and s[i].isspace():
                    i += 1
                advanced = True
        if not advanced:
            break

    head = s[i:]
    if not head.startswith(start_literal):
        fail(f"{name}: start does not match expected pattern: {start_literal!r}")
    m = re.search(r'@if\s*\([^\)]*\)\s*\{', head)
    if not m:
        fail(f"{name}: missing '{{' after {start_literal}")


def validate_and_maybe_fix(name: str, expected: str, start_literal: str) -> str:
    ensure_starts_with_if_block(name, expected, start_literal)
    ok, opens, closes = brace_balance(expected)
    if ok:
        return expected
    delta = opens - closes

    if delta > 0 and AUTO_FIX_BRACES:
        fixed = expected.rstrip() + ("\n" + "}" * delta) + "\n"
        ok2, o2, c2 = brace_balance(fixed)
        if ok2:
            print(f"[INFO] {name}: appended {delta} closing brace(s).")
            return fixed
        fail(f"{name}: brace auto-fix failed (opens={o2}, closes={c2})")

    if delta < 0:
        # trim surplus trailing braces if present
        need = -delta
        t = expected.rstrip()
        trimmed = 0
        while trimmed < need and t.endswith("}"):
            t = t[:-1].rstrip()
            trimmed += 1
        if trimmed == need:
            ok2, o2, c2 = brace_balance(t)
            if ok2:
                print(f"[INFO] {name}: trimmed {trimmed} surplus closing brace(s).")
                return t
        fail(f"{name}: brace mismatch (opens={opens}, closes={closes}). Surplus closers not only at EOF.")
    fail(f"{name}: brace mismatch (opens={opens}, closes={closes}). Set AUTO_FIX_BRACES=true to auto-append missing '}}'.")


def compare(section_name: str, expected: str, split_file: Path):
    got_raw = read(split_file)

    # 1) Remove REGION wrappers from the split file only
    got_core = strip_region_wrappers(got_raw)

    # 2) Normalize head noise on BOTH sides (BOM, leading blank lines, leading HTML/Razor comments)
    exp_n = _strip_leading_noise(expected)
    got_n = _strip_leading_noise(got_core)

    # 3) Optionally also normalize whitespace for robust matching
    if norm_ws(exp_n) == norm_ws(got_n):
        print(f"[INFO] {section_name}: OK")
        return

    # 4) Show diff on normalized text so the diff is meaningful
    exp_lines = exp_n.splitlines(keepends=False)
    got_lines = got_n.splitlines(keepends=False)
    diff = "\n".join(difflib.unified_diff(exp_lines, got_lines, fromfile="expected", tofile=str(split_file), lineterm=""))
    print(f"\nDIFF for {section_name}:\n{diff}\n")
    fail(f"{section_name}: mismatch. See diff above.")


def fail(msg: str):
    print(f"ERROR: {msg}")
    sys.exit(1)

def _strip_leading_noise(s: str) -> str:
    s = s.lstrip("\ufeff")  # BOM
    lines = s.splitlines()
    i = 0
    # drop leading blank lines
    while i < len(lines) and lines[i].strip() == "":
        i += 1
    # drop leading HTML/Razor comments
    while i < len(lines):
        line = lines[i].lstrip()
        if line.startswith("<!--"):
            # skip HTML comment
            j = i
            closed = False
            while j < len(lines):
                if "-->" in lines[j]:
                    i = j + 1
                    closed = True
                    break
                j += 1
            if not closed:
                break
            while i < len(lines) and lines[i].strip() == "":
                i += 1
            continue
        if line.startswith("@*"):
            # skip Razor comment
            j = i
            closed = False
            while j < len(lines):
                if "*@" in lines[j]:
                    i = j + 1
                    closed = True
                    break
                j += 1
            if not closed:
                break
            while i < len(lines) and lines[i].strip() == "":
                i += 1
            continue
        break
    return "\n".join(lines[i:])


# ---------------- extractors ----------------

def extract_student(monolith: str) -> str:
    s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsStudentUser)\n{')
    if s_start is None:
        s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsStudentUser)\n                                {')
    if s_start is None:
        fail("Student start not found")

    # end anchor
    _, btn_end = find_ws_insensitive(
        monolith[s_start:], 
        '<button type="button" class="btn btn-secondary" @onclick="CloseJobDetailsModal">'
    )
    if btn_end is None:
        fail("Student end anchor button not found")
    btn_abs_end = s_start + btn_end

    # allow two or three braces after the four </div>
    closing = re.compile(
        r"</div>\s*</div>\s*</div>\s*</div>\s*}\s*}\s*(?:}\s*)?",
        re.DOTALL,
    )
    m = closing.search(monolith[btn_abs_end:])
    if not m:
        fail("Student closing structure not found")

    expected = monolith[s_start:btn_abs_end + m.end()]
    return validate_and_maybe_fix("Student", expected, "@if (!isInitializedAsStudentUser)")


def extract_company(monolith: str) -> str:
    s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsCompanyUser)\n{')
    if s_start is None:
        s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsCompanyUser)\n                                {')
    if s_start is None:
        fail("Company start not found")

    _, btn_end = find_ws_insensitive(monolith[s_start:], '@onclick="CloseModalResearchGroupDetailsOnEyeIconWhenSearchForResearchGroupsAsCompany"')
    if btn_end is None:
        fail("Company end anchor button not found")
    btn_abs_end = s_start + btn_end

    closing = re.compile(
    r"</div>\s*</div>\s*</div>\s*</div>\s*}",
    re.DOTALL
    )

    m = closing.search(monolith[btn_abs_end:])
    if not m:
        fail("Company closing structure not found")
    expected = monolith[s_start:btn_abs_end + m.end()]

    return validate_and_maybe_fix("Company", expected, "@if (!isInitializedAsCompanyUser)")

def extract_professor(monolith: str) -> str:
    s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsProfessorUser)\n{')
    if s_start is None:
        s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsProfessorUser)\n                                {')
    if s_start is None:
        fail("Professor start not found")

    _, canv_end = find_ws_insensitive(monolith[s_start:], '<canvas id="skillsChart"')
    if canv_end is None:
        fail("Professor end anchor canvas not found")
    canv_abs_end = s_start + canv_end

    # A) </div></div></div></div><br/><br/>
    tail_a = re.compile(r"</div>\s*</div>\s*</div>\s*</div>\s*<br/>\s*<br/>\s*", re.DOTALL)
    # B) </div></div> } </div></div></div> <br/><br/>
    tail_b = re.compile(r"</div>\s*</div>\s*}\s*</div>\s*</div>\s*</div>\s*<br/>\s*<br/>\s*", re.DOTALL)
    # C) </div></div></div> <br/><br/> }
    tail_c = re.compile(r"</div>\s*</div>\s*</div>\s*<br/>\s*<br/>\s*}\s*", re.DOTALL)

    segment = monolith[canv_abs_end:]
    m = tail_a.search(segment) or tail_b.search(segment) or tail_c.search(segment)
    if not m:
        fail("Professor closing structure not found")

    expected = monolith[s_start:canv_abs_end + m.end()]
    return validate_and_maybe_fix("Professor", expected, "@if (!isInitializedAsProfessorUser)")

def extract_research(monolith: str) -> str:
    s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsResearchGroupUser)\n{')
    if s_start is None:
        s_start, _ = find_ws_insensitive(monolith, '@if (!isInitializedAsResearchGroupUser)\n                                {')
    if s_start is None:
        fail("ResearchGroup start not found")

    _, btn_end = find_ws_insensitive(monolith[s_start:], '@onclick="ClosePatentsModal"')
    if btn_end is None:
        fail("ResearchGroup end anchor button not found")
    btn_abs_end = s_start + btn_end

    m = re.search(r"</div>\s*</div>\s*</div>\s*</div>\s*}", monolith[btn_abs_end:], flags=re.DOTALL)
    if not m:
        fail("ResearchGroup closing structure not found")

    expected = monolith[s_start:btn_abs_end + m.end()]
    return validate_and_maybe_fix("ResearchGroup", expected, "@if (!isInitializedAsResearchGroupUser)")

# ---------------- main ----------------

def main():
    mono = read(MONOLITH)
    compare("Student",       extract_student(mono),   STUDENT)
    compare("Company",       extract_company(mono),   COMPANY)
    compare("Professor",     extract_professor(mono), PROF)
    compare("ResearchGroup", extract_research(mono),  RGROUP)
    print("[INFO] All sections match.")
    return 0

if __name__ == "__main__":
    sys.exit(main())