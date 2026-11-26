#!/usr/bin/env python3
"""Deterministic UI/UX audit for frontend source trees.

Runs the mechanical checks that would otherwise be re-derived by hand on every
UI task: color contrast (WCAG 2.x + APCA Lc), smooth-scroll and reduced-motion
pairing, in-page navigation wiring, sticky headers, back-to-top affordances,
touch target sizes, focus visibility, and layout-thrashing animations.

Standard library only, so it runs on Windows and Linux without installing
anything. The audit never writes to the scanned tree; --output is the only
write path and it honours --dry-run.
"""

from __future__ import annotations

import argparse
import csv
import json
import math
import re
import sys
from dataclasses import dataclass, field
from pathlib import Path

SCAN_SUFFIXES = {
    ".html", ".htm", ".vue", ".svelte", ".astro",
    ".jsx", ".tsx", ".js", ".ts", ".mjs", ".cjs",
    ".css", ".scss", ".sass", ".less",
}

SKIP_DIRS = {
    "node_modules", ".git", "dist", "build", "out", ".next", ".nuxt",
    "coverage", "__pycache__", ".venv", "venv", ".cache", "vendor",
    ".svelte-kit", ".output", "target",
}

# Severity ordering used for sorting and for the --fail-on gate.
SEVERITY_ORDER = {"error": 0, "warning": 1, "info": 2}

MAX_FILE_BYTES = 2_000_000

# --- WCAG 2.x thresholds (W3C Recommendation, current normative standard) ---
WCAG_AA_NORMAL = 4.5
WCAG_AA_LARGE = 3.0
WCAG_AAA_NORMAL = 7.0
WCAG_NON_TEXT = 3.0

# --- APCA Lc guidance (WCAG 3 draft, informative only; see references) ---
APCA_BODY_TEXT = 75.0
APCA_LARGE_TEXT = 60.0
APCA_NON_TEXT = 45.0


@dataclass
class Finding:
    """One audit result anchored to a file and line."""

    severity: str
    check: str
    file: str
    line: int
    message: str
    hint: str = ""

    def as_row(self) -> dict[str, str]:
        return {
            "severity": self.severity,
            "check": self.check,
            "file": self.file,
            "line": str(self.line),
            "message": self.message,
            "hint": self.hint,
        }


@dataclass
class ScanContext:
    """Whole-tree signals that only make sense after every file is read."""

    files_scanned: int = 0
    anchor_targets: set[str] = field(default_factory=set)
    hash_links: list[tuple[str, int, str]] = field(default_factory=list)
    has_smooth_scroll: bool = False
    smooth_scroll_sites: list[tuple[str, int]] = field(default_factory=list)
    smooth_scroll_guarded: bool = False
    has_scroll_margin: bool = False
    has_sticky: bool = False
    has_back_to_top: bool = False
    has_reduced_motion: bool = False
    has_focus_visible: bool = False
    has_skip_link: bool = False
    has_scroll_spy: bool = False
    has_transition_or_animation: bool = False
    has_multi_section_page: bool = False


# ---------------------------------------------------------------------------
# Color math
# ---------------------------------------------------------------------------

HEX_RE = re.compile(r"#([0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b")
RGB_FUNC_RE = re.compile(
    r"\brgba?\(\s*(\d{1,3})[\s,]+(\d{1,3})[\s,]+(\d{1,3})",
    re.IGNORECASE,
)
# oklch(L C H) with L as 0-1 or a percentage; alpha after "/" is ignored.
OKLCH_FUNC_RE = re.compile(
    r"\boklch\(\s*([\d.]+)(%?)\s+([\d.]+)\s+([\d.]+)(?:deg)?\s*(?:/[^)]*)?\)",
    re.IGNORECASE,
)


def oklch_to_srgb(lightness: float, chroma: float, hue_deg: float) -> tuple[int, int, int]:
    """Convert OKLCH to gamut-clipped 8-bit sRGB (Björn Ottosson's OKLab matrices)."""

    hue = math.radians(hue_deg)
    a = chroma * math.cos(hue)
    b = chroma * math.sin(hue)
    l_ = (lightness + 0.3963377774 * a + 0.2158037573 * b) ** 3
    m_ = (lightness - 0.1055613458 * a - 0.0638541728 * b) ** 3
    s_ = (lightness - 0.0894841775 * a - 1.2914855480 * b) ** 3
    linear = (
        4.0767416621 * l_ - 3.3077115913 * m_ + 0.2309699292 * s_,
        -1.2684380046 * l_ + 2.6097574011 * m_ - 0.3413193965 * s_,
        -0.0041960863 * l_ - 0.7034186147 * m_ + 1.7076147010 * s_,
    )

    def encode(channel: float) -> int:
        channel = min(1.0, max(0.0, channel))
        value = 12.92 * channel if channel <= 0.0031308 else 1.055 * channel ** (1 / 2.4) - 0.055
        return int(round(value * 255))

    return (encode(linear[0]), encode(linear[1]), encode(linear[2]))


def parse_color(value: str) -> tuple[int, int, int] | None:
    """Parse #hex, rgb()/rgba() or oklch() into 8-bit sRGB."""

    text = value.strip()
    oklch = OKLCH_FUNC_RE.match(text)
    if oklch:
        lightness = float(oklch.group(1)) / (100.0 if oklch.group(2) else 1.0)
        return oklch_to_srgb(lightness, float(oklch.group(3)), float(oklch.group(4)))
    match = HEX_RE.fullmatch(text) or HEX_RE.match(text)
    if match:
        digits = match.group(1)
        if len(digits) in (3, 4):
            digits = "".join(ch * 2 for ch in digits[:3])
        digits = digits[:6]
        return (int(digits[0:2], 16), int(digits[2:4], 16), int(digits[4:6], 16))

    func = RGB_FUNC_RE.match(text)
    if func:
        parts = [min(255, max(0, int(func.group(i)))) for i in (1, 2, 3)]
        return (parts[0], parts[1], parts[2])
    return None


def _srgb_to_linear(channel: float) -> float:
    c = channel / 255.0
    return c / 12.92 if c <= 0.04045 else ((c + 0.055) / 1.055) ** 2.4


def relative_luminance(rgb: tuple[int, int, int]) -> float:
    """WCAG 2.x relative luminance (WCAG 2.2, Understanding SC 1.4.3)."""

    r, g, b = (_srgb_to_linear(c) for c in rgb)
    return 0.2126 * r + 0.7152 * g + 0.0722 * b


def contrast_ratio(fg: tuple[int, int, int], bg: tuple[int, int, int]) -> float:
    """WCAG 2.x contrast ratio, 1.0 to 21.0."""

    l1 = relative_luminance(fg)
    l2 = relative_luminance(bg)
    lighter, darker = (l1, l2) if l1 >= l2 else (l2, l1)
    return (lighter + 0.05) / (darker + 0.05)


# APCA 0.1.9 "0.0.98G-4g-base-W3" constants.
# Informative only: WCAG 3 is a Working Draft and has not finalised a contrast
# method, so this is reported alongside - never instead of - WCAG 2.
_APCA_MAIN_TRC = 2.4
_APCA_NORM_TXT = 0.57  # normal polarity (dark text on light bg): text exponent
_APCA_NORM_BG = 0.56  # normal polarity: background exponent
_APCA_REV_TXT = 0.62  # reverse polarity (light text on dark bg): text exponent
_APCA_REV_BG = 0.65  # reverse polarity: background exponent
_APCA_BLK_THRS = 0.022  # soft black clamp threshold
_APCA_BLK_CLMP = 1.414  # soft black clamp exponent
_APCA_SCALE = 1.14  # W_scale
_APCA_OFFSET = 0.027  # W_offset
_APCA_LO_CLIP = 0.1  # W_clamp
_APCA_DELTA_Y_MIN = 0.0005


def _apca_y(rgb: tuple[int, int, int]) -> float:
    """Screen luminance estimate using APCA sRGB coefficients."""

    r, g, b = ((c / 255.0) ** _APCA_MAIN_TRC for c in rgb)
    return 0.2126729 * r + 0.7151522 * g + 0.0721750 * b


def _apca_soft_clamp(y: float) -> float:
    """Soft-clamp near-black luminance to model flare on real displays."""

    if y < 0.0:
        return 0.0
    if y < _APCA_BLK_THRS:
        return y + (_APCA_BLK_THRS - y) ** _APCA_BLK_CLMP
    return y


def apca_lc(text_rgb: tuple[int, int, int], bg_rgb: tuple[int, int, int]) -> float:
    """APCA lightness contrast (Lc). Sign shows polarity; magnitude is readability.

    Positive Lc is dark text on a light background; negative is light on dark.
    """

    y_text = _apca_soft_clamp(_apca_y(text_rgb))
    y_bg = _apca_soft_clamp(_apca_y(bg_rgb))

    if abs(y_bg - y_text) < _APCA_DELTA_Y_MIN:
        return 0.0

    if y_bg > y_text:  # normal polarity: dark text on light background
        contrast = (y_bg**_APCA_NORM_BG - y_text**_APCA_NORM_TXT) * _APCA_SCALE
        output = 0.0 if contrast < _APCA_LO_CLIP else contrast - _APCA_OFFSET
    else:  # reverse polarity: light text on dark background
        contrast = (y_bg**_APCA_REV_BG - y_text**_APCA_REV_TXT) * _APCA_SCALE
        output = 0.0 if contrast > -_APCA_LO_CLIP else contrast + _APCA_OFFSET

    return output * 100.0


# ---------------------------------------------------------------------------
# Pattern definitions
# ---------------------------------------------------------------------------

ANCHOR_ID_RE = re.compile(r"""\bid\s*=\s*["'{]?\s*([A-Za-z][\w:.-]*)""")
HASH_LINK_RE = re.compile(r"""href\s*=\s*["'{`]?\s*#([A-Za-z][\w:.-]*)""")

SMOOTH_SCROLL_RE = re.compile(
    r"scroll-behavior\s*:\s*smooth|scrollBehavior\s*:\s*[\"']smooth[\"']"
    r"|behavior\s*:\s*[\"']smooth[\"']|scroll-smooth\b",
    re.IGNORECASE,
)
REDUCED_MOTION_RE = re.compile(
    r"prefers-reduced-motion|motion-safe:|motion-reduce:|useReducedMotion",
    re.IGNORECASE,
)
NO_PREFERENCE_RE = re.compile(
    r"prefers-reduced-motion\s*:\s*no-preference|motion-safe:", re.IGNORECASE
)
SCROLL_MARGIN_RE = re.compile(
    r"scroll-margin-top|scroll-mt-|scroll-padding-top|scroll-pt-|scrollMarginTop",
    re.IGNORECASE,
)
STICKY_RE = re.compile(
    r"position\s*:\s*sticky|(?<![\w-])sticky(?![\w-])|position\s*:\s*fixed", re.IGNORECASE
)
BACK_TO_TOP_RE = re.compile(
    r"back[-_\s]?to[-_\s]?top|scrollToTop|scroll_to_top|トップへ戻る|ページ先頭|#top\b",
    re.IGNORECASE,
)
SCROLL_SPY_RE = re.compile(
    r"IntersectionObserver|scrollspy|scroll-spy|useActiveSection|activeSection",
    re.IGNORECASE,
)
SKIP_LINK_RE = re.compile(
    r"skip[-_\s]?(to[-_\s]?)?(main|content|nav)|スキップリンク|本文へ", re.IGNORECASE
)
FOCUS_VISIBLE_RE = re.compile(r":focus-visible|focus-visible:|focus-within:", re.IGNORECASE)
OUTLINE_NONE_RE = re.compile(
    r"outline\s*:\s*(none|0)\b|\boutline-none\b|\bfocus:outline-none\b", re.IGNORECASE
)
TRANSITION_RE = re.compile(
    r"\btransition\b|\banimation\b|@keyframes|\btransition-|\banimate-", re.IGNORECASE
)

# Animating these forces layout on every frame; transform/opacity do not.
LAYOUT_ANIM_RE = re.compile(
    r"transition\s*(?:-property)?\s*:\s*[^;{]*\b(width|height|top|left|right|bottom|margin|padding)\b"
    r"|transition-\[(?:[^\]]*\b(?:width|height|top|left|margin|padding)\b[^\]]*)\]"
    r"|transition-all\b",
    re.IGNORECASE,
)

SECTION_RE = re.compile(r"<section\b|<article\b", re.IGNORECASE)

# Tailwind spacing -> px (default scale, 1 unit = 4px)
TW_SIZE_RE = re.compile(r"(?<![\w-])(?:h|w|size)-(\d{1,2})(?![\w.-])")
CLICKABLE_HINT_RE = re.compile(
    r"<button\b|role\s*=\s*[\"']button[\"']|onClick|<a\b|@click|v-on:click", re.IGNORECASE
)

FIXED_PX_FONT_RE = re.compile(r"font-size\s*:\s*(\d+(?:\.\d+)?)px", re.IGNORECASE)

# --- Spacing rhythm / readability -----------------------------------------
# A heading must sit closer to the content it introduces than to the block
# above it (Gestalt proximity). Below this top margin we assume the heading is
# leaning on a container `gap` and is therefore visually equidistant.
HEADING_MIN_TOP_REM = 1.5
MAX_COMFORTABLE_CH = 80.0
MIN_BODY_LINE_HEIGHT = 1.4

# Match a CSS rule block so we can inspect its selector and declarations.
HEADING_RULE_RE = re.compile(
    r"(?P<selector>[^{}/;]+?)\{(?P<body>[^{}]*)\}",
    re.MULTILINE,
)
# CSS Modules class names like `.sectionTitle` count as headings too.
HEADING_SELECTOR_RE = re.compile(
    r"(?<![\w-])h[1-6](?![\w-])|title|heading|headline|sectionTitle",
    re.IGNORECASE,
)
MARGIN_DECL_RE = re.compile(r"(?<![-\w])margin(?:-top|-block|-block-start)?\s*:", re.IGNORECASE)
MARGIN_SHORTHAND_RE = re.compile(r"(?<![-\w])margin\s*:\s*(?P<value>[^;}]+)", re.IGNORECASE)
MARGIN_TOP_RE = re.compile(
    r"(?<![-\w])margin-(?:top|block-start)\s*:\s*(?P<value>[^;}]+)", re.IGNORECASE
)
MARGIN_BOTTOM_RE = re.compile(
    r"(?<![-\w])margin-(?:bottom|block-end)\s*:\s*(?P<value>[^;}]+)", re.IGNORECASE
)
MARGIN_BLOCK_RE = re.compile(r"(?<![-\w])margin-block\s*:\s*(?P<value>[^;}]+)", re.IGNORECASE)
MAX_WIDTH_CH_RE = re.compile(r"max-width\s*:\s*(\d+(?:\.\d+)?)ch", re.IGNORECASE)
UNITLESS_LINE_HEIGHT_RE = re.compile(r"line-height\s*:\s*(\d(?:\.\d+)?)\s*[;}]", re.IGNORECASE)
HEADING_CONTEXT_RE = re.compile(
    r"h[1-6]|title|heading|hero|display|brand|logo|mark|icon|badge|number|count",
    re.IGNORECASE,
)
# Running text is set at body size or larger and usually wraps.
BODY_TEXT_HINT_RE = re.compile(
    r"font-size\s*:\s*var\(--text-(?:base|lg|md)\)|font-size\s*:\s*1(?:\.0+)?rem"
    r"|font-size\s*:\s*1\.1\d*rem|max-width\s*:\s*\d+(?:ch|rem)",
    re.IGNORECASE,
)

# Spacing tokens are usually `--space-6: 1.5rem`, so a bare var() tells us
# nothing numerically. Treat those as "unknown" rather than guessing.
_REM_RE = re.compile(r"^(-?\d+(?:\.\d+)?)rem$")
_PX_RE = re.compile(r"^(-?\d+(?:\.\d+)?)px$")


def length_to_rem(token: str) -> float | None:
    """Convert a single CSS length token to rem, or None if not resolvable."""

    value = token.strip().lower()
    if value in {"0", "0rem", "0px", "0em"}:
        return 0.0
    match = _REM_RE.match(value)
    if match:
        return float(match.group(1))
    match = _PX_RE.match(value)
    if match:
        # Assume the conventional 16px root so px and rem stay comparable.
        return float(match.group(1)) / 16.0
    return None


def _shorthand_parts(value: str) -> list[str]:
    return [part for part in re.split(r"\s+", value.strip()) if part]


def selector_line(text: str, block_start: int, selector: str) -> int:
    """1-indexed line of a rule's selector.

    The rule regex starts matching at the whitespace left over from the previous
    block, so counting newlines at ``block_start`` reports the line above.
    Skipping that leading whitespace puts the finding on the selector itself.
    """

    offset = block_start + (len(selector) - len(selector.lstrip()))
    return text[:offset].count("\n") + 1


def margin_top_of(body: str) -> float | None:
    """Resolve the effective top margin of a declaration block, in rem."""

    match = MARGIN_TOP_RE.search(body)
    if match:
        return length_to_rem(match.group("value"))
    match = MARGIN_BLOCK_RE.search(body)
    if match:
        parts = _shorthand_parts(match.group("value"))
        if parts:
            return length_to_rem(parts[0])
    match = MARGIN_SHORTHAND_RE.search(body)
    if match:
        parts = _shorthand_parts(match.group("value"))
        if parts:
            return length_to_rem(parts[0])
    return None


def margin_bottom_of(body: str) -> float | None:
    """Resolve the effective bottom margin of a declaration block, in rem."""

    match = MARGIN_BOTTOM_RE.search(body)
    if match:
        return length_to_rem(match.group("value"))
    match = MARGIN_BLOCK_RE.search(body)
    if match:
        parts = _shorthand_parts(match.group("value"))
        if parts:
            # `margin-block: a` sets both; `margin-block: a b` sets start/end.
            return length_to_rem(parts[1] if len(parts) > 1 else parts[0])
    match = MARGIN_SHORTHAND_RE.search(body)
    if match:
        parts = _shorthand_parts(match.group("value"))
        if not parts:
            return None
        # margin: a | a b | a b c | a b c d  -> bottom is index 2 when present.
        if len(parts) == 1:
            return length_to_rem(parts[0])
        if len(parts) == 2:
            return length_to_rem(parts[0])
        return length_to_rem(parts[2])
    return None

# Pairs of colors declared close together are the realistic contrast candidates.
COLOR_DECL_RE = re.compile(
    r"(?P<prop>(?<![-\w])(?:color|background-color|background|border-color))\s*:\s*"
    r"(?P<value>#[0-9a-fA-F]{3,8}|rgba?\([^)]*\))",
    re.IGNORECASE,
)


def is_probably_text_prop(prop: str) -> bool:
    return prop.strip().lower() == "color"


# ---------------------------------------------------------------------------
# File scanning
# ---------------------------------------------------------------------------


def iter_files(root: Path, extra_skip: set[str]) -> list[Path]:
    """Collect scannable files, skipping build output and vendor trees."""

    if root.is_file():
        return [root] if root.suffix.lower() in SCAN_SUFFIXES else []

    skip = SKIP_DIRS | extra_skip
    found: list[Path] = []
    for path in sorted(root.rglob("*")):
        if not path.is_file():
            continue
        if path.suffix.lower() not in SCAN_SUFFIXES:
            continue
        if any(part in skip for part in path.parts):
            continue
        found.append(path)
    return found


def read_text(path: Path) -> str | None:
    """Read a file as UTF-8, skipping anything too large or unreadable."""

    try:
        if path.stat().st_size > MAX_FILE_BYTES:
            return None
        return path.read_text(encoding="utf-8", errors="replace")
    except OSError:
        return None


def rel(path: Path, root: Path) -> str:
    try:
        return path.relative_to(root).as_posix()
    except ValueError:
        return path.as_posix()


# ---------------------------------------------------------------------------
# Checks
# ---------------------------------------------------------------------------


def check_contrast(text: str, name: str, findings: list[Finding]) -> None:
    """Compare color declarations that appear within a few lines of each other."""

    lines = text.splitlines()
    decls: list[tuple[int, str, tuple[int, int, int]]] = []
    for index, line in enumerate(lines, start=1):
        for match in COLOR_DECL_RE.finditer(line):
            rgb = parse_color(match.group("value"))
            if rgb is not None:
                decls.append((index, match.group("prop").lower(), rgb))

    for i, (line_no, prop, rgb) in enumerate(decls):
        if not is_probably_text_prop(prop):
            continue
        # Look for the nearest background declared in the same rule block.
        for other_line, other_prop, other_rgb in decls[max(0, i - 4) : i + 5]:
            if is_probably_text_prop(other_prop):
                continue
            if abs(other_line - line_no) > 6:
                continue
            ratio = contrast_ratio(rgb, other_rgb)
            lc = apca_lc(rgb, other_rgb)
            if ratio < WCAG_AA_LARGE:
                findings.append(
                    Finding(
                        "error",
                        "contrast-wcag",
                        name,
                        line_no,
                        f"Contrast {ratio:.2f}:1 (APCA Lc {lc:.0f}) between "
                        f"#{rgb[0]:02x}{rgb[1]:02x}{rgb[2]:02x} and "
                        f"#{other_rgb[0]:02x}{other_rgb[1]:02x}{other_rgb[2]:02x} "
                        f"is below the 3:1 large-text floor",
                        "WCAG 2.2 SC 1.4.3 needs 4.5:1 for body text, 3:1 for large text",
                    )
                )
            elif ratio < WCAG_AA_NORMAL:
                findings.append(
                    Finding(
                        "warning",
                        "contrast-wcag",
                        name,
                        line_no,
                        f"Contrast {ratio:.2f}:1 (APCA Lc {lc:.0f}) passes only for "
                        f"large text (>=18.66px bold or >=24px)",
                        "Body text needs 4.5:1. Darken the text or lighten the surface",
                    )
                )
            elif abs(lc) < APCA_LARGE_TEXT:
                findings.append(
                    Finding(
                        "info",
                        "contrast-apca",
                        name,
                        line_no,
                        f"WCAG {ratio:.2f}:1 passes but APCA Lc {lc:.0f} is thin for "
                        f"small text (Lc 75 preferred for body)",
                        "APCA is informative (WCAG 3 draft). Treat as a readability ceiling",
                    )
                )
            break


def check_focus(text: str, name: str, findings: list[Finding]) -> None:
    """Flag removed focus outlines that are not replaced by a visible ring."""

    lines = text.splitlines()
    for index, line in enumerate(lines, start=1):
        if not OUTLINE_NONE_RE.search(line):
            continue
        window = "\n".join(lines[max(0, index - 4) : index + 4])
        has_replacement = (
            FOCUS_VISIBLE_RE.search(window)
            or re.search(r"box-shadow|ring-\d|ring-offset|border-", window, re.IGNORECASE)
        )
        if not has_replacement:
            findings.append(
                Finding(
                    "error",
                    "focus-visible",
                    name,
                    index,
                    "Focus outline removed with no visible replacement nearby",
                    "WCAG 2.2 SC 2.4.7/2.4.11: pair outline:none with :focus-visible ring",
                )
            )


def check_touch_targets(text: str, name: str, findings: list[Finding]) -> None:
    """Flag Tailwind-sized interactive elements below the 24px minimum."""

    for index, line in enumerate(text.splitlines(), start=1):
        if not CLICKABLE_HINT_RE.search(line):
            continue
        sizes = [int(m.group(1)) * 4 for m in TW_SIZE_RE.finditer(line)]
        if not sizes:
            continue
        smallest = min(sizes)
        if smallest < 24:
            findings.append(
                Finding(
                    "error",
                    "touch-target",
                    name,
                    index,
                    f"Interactive element sized ~{smallest}px, below the 24x24 CSS px minimum",
                    "WCAG 2.2 SC 2.5.8 (AA) requires 24px; 44px is the comfortable target",
                )
            )
        elif smallest < 44:
            findings.append(
                Finding(
                    "info",
                    "touch-target",
                    name,
                    index,
                    f"Interactive element ~{smallest}px: meets SC 2.5.8 but below 44px comfort target",
                    "Enlarge or add padding for primary touch controls",
                )
            )


def check_layout_animation(text: str, name: str, findings: list[Finding]) -> None:
    """Flag transitions on properties that trigger layout every frame."""

    for index, line in enumerate(text.splitlines(), start=1):
        match = LAYOUT_ANIM_RE.search(line)
        if not match:
            continue
        is_transition_all = "transition-all" in match.group(0).lower()
        findings.append(
            Finding(
                "info" if is_transition_all else "warning",
                "animation-perf",
                name,
                index,
                (
                    "transition-all animates every changed property, including "
                    "layout-triggering ones"
                    if is_transition_all
                    else f"Transition targets a layout property: {match.group(0).strip()[:70]}"
                ),
                "Animate transform/opacity to stay on the compositor and hold 60fps",
            )
        )


def check_heading_proximity(text: str, name: str, findings: list[Finding]) -> None:
    """Flag headings whose top margin is zero inside a flow of body text.

    Gestalt proximity: a heading belongs to the content *below* it, so the gap
    above must be clearly larger than the gap below. When a heading rule sets
    ``margin: 0`` (or only a bottom margin) and the surrounding container relies
    on a uniform ``gap``, the heading ends up equidistant from -- or closer to --
    the *previous* block. Readers then parse it as the tail of the section above,
    which is exactly the "cramped" look this check exists to prevent.
    """

    blocks = HEADING_RULE_RE.finditer(text)
    for match in blocks:
        selector = match.group("selector")
        body = match.group("body")
        # Only judge rules that actually style a heading-ish element.
        if not HEADING_SELECTOR_RE.search(selector):
            continue
        # A rule that never touches margin isn't making a spacing claim.
        margin_decl = MARGIN_DECL_RE.search(body)
        if not margin_decl:
            continue

        top = margin_top_of(body)
        bottom = margin_bottom_of(body)
        if top is None or bottom is None:
            continue
        # The heading must "own" the content beneath it.
        if top > bottom or top >= HEADING_MIN_TOP_REM:
            continue
        # A heading with no bottom margin either isn't separating anything or is
        # spaced by its container; only a real bottom gap proves the rule is
        # doing the spacing itself, which is when the asymmetry actually shows.
        if bottom <= 0:
            continue

        line = selector_line(text, match.start(), selector)
        findings.append(
            Finding(
                "warning",
                "spacing-rhythm",
                name,
                line,
                (
                    f"Heading rule '{selector.strip()[:44]}' has margin-top "
                    f"{top}rem vs margin-bottom {bottom}rem, so it sits as close to "
                    f"the previous block as to its own content"
                ),
                (
                    "Give headings a larger top margin than bottom (roughly 2:1) so "
                    "proximity groups each heading with the text it introduces"
                ),
            )
        )


def check_line_length(text: str, name: str, findings: list[Finding]) -> None:
    """Flag prose containers that allow uncomfortably long measures."""

    for index, line in enumerate(text.splitlines(), start=1):
        match = MAX_WIDTH_CH_RE.search(line)
        if match:
            width = float(match.group(1))
            if width > MAX_COMFORTABLE_CH:
                findings.append(
                    Finding(
                        "info",
                        "readability",
                        name,
                        index,
                        f"Text container allows {match.group(1)}ch per line",
                        "Keep running text near 45-75ch; long measures lose the return sweep",
                    )
                )


def check_line_height(text: str, name: str, findings: list[Finding]) -> None:
    """Flag *body* text set with a line-height too tight to scan comfortably.

    Only rules that also declare a body-sized font are considered. Single-line
    UI furniture (icons, badges, brand lockups) legitimately runs at 1.0 and
    would otherwise drown the signal.
    """

    for match in HEADING_RULE_RE.finditer(text):
        selector = match.group("selector")
        body = match.group("body")
        lh_match = UNITLESS_LINE_HEIGHT_RE.search(body)
        if not lh_match:
            continue
        value = float(lh_match.group(1))
        if value >= MIN_BODY_LINE_HEIGHT:
            continue
        # Headings and display type are meant to be tight.
        if HEADING_CONTEXT_RE.search(selector):
            continue
        # Require evidence this rule styles running text, not a one-line label.
        if not BODY_TEXT_HINT_RE.search(body):
            continue

        line = selector_line(text, match.start(), selector)
        findings.append(
            Finding(
                "info",
                "readability",
                name,
                line,
                f"'{selector.strip()[:44]}' sets line-height {lh_match.group(1)} on body text",
                "Body copy reads best around 1.5-1.8; reserve <1.3 for headings",
            )
        )


def check_fixed_font(text: str, name: str, findings: list[Finding]) -> None:
    """Flag px font sizes that ignore the user's browser font-size setting."""

    for index, line in enumerate(text.splitlines(), start=1):
        for match in FIXED_PX_FONT_RE.finditer(line):
            size = float(match.group(1))
            if size < 16:
                findings.append(
                    Finding(
                        "warning",
                        "typography",
                        name,
                        index,
                        f"font-size: {match.group(1)}px is below the 16px body baseline "
                        f"and ignores user font scaling",
                        "Use rem so the user's browser font-size setting still applies",
                    )
                )


def scan_file(path: Path, root: Path, ctx: ScanContext, findings: list[Finding]) -> None:
    """Run every per-file check and accumulate whole-tree signals."""

    text = read_text(path)
    if text is None:
        return
    ctx.files_scanned += 1
    name = rel(path, root)
    suffix = path.suffix.lower()

    for match in ANCHOR_ID_RE.finditer(text):
        ctx.anchor_targets.add(match.group(1))
    for index, line in enumerate(text.splitlines(), start=1):
        for match in HASH_LINK_RE.finditer(line):
            target = match.group(1)
            if target.lower() != "top":
                ctx.hash_links.append((name, index, target))

    if SMOOTH_SCROLL_RE.search(text):
        ctx.has_smooth_scroll = True
        for index, line in enumerate(text.splitlines(), start=1):
            if SMOOTH_SCROLL_RE.search(line):
                ctx.smooth_scroll_sites.append((name, index))
        if NO_PREFERENCE_RE.search(text):
            ctx.smooth_scroll_guarded = True
    if SCROLL_MARGIN_RE.search(text):
        ctx.has_scroll_margin = True
    if STICKY_RE.search(text):
        ctx.has_sticky = True
    if BACK_TO_TOP_RE.search(text):
        ctx.has_back_to_top = True
    if REDUCED_MOTION_RE.search(text):
        ctx.has_reduced_motion = True
    if FOCUS_VISIBLE_RE.search(text):
        ctx.has_focus_visible = True
    if SKIP_LINK_RE.search(text):
        ctx.has_skip_link = True
    if SCROLL_SPY_RE.search(text):
        ctx.has_scroll_spy = True
    if TRANSITION_RE.search(text):
        ctx.has_transition_or_animation = True
    if len(SECTION_RE.findall(text)) >= 3:
        ctx.has_multi_section_page = True

    if suffix in {".css", ".scss", ".sass", ".less", ".html", ".htm", ".vue", ".svelte", ".astro"}:
        check_contrast(text, name, findings)
        check_fixed_font(text, name, findings)
        check_heading_proximity(text, name, findings)
        check_line_length(text, name, findings)
        check_line_height(text, name, findings)
    check_focus(text, name, findings)
    check_touch_targets(text, name, findings)
    check_layout_animation(text, name, findings)


def check_project_level(ctx: ScanContext, findings: list[Finding]) -> None:
    """Checks that only make sense once the whole tree has been read."""

    known = ctx.anchor_targets
    seen: set[tuple[str, str]] = set()
    for name, line, target in ctx.hash_links:
        if target in known:
            continue
        if (name, target) in seen:
            continue
        seen.add((name, target))
        findings.append(
            Finding(
                "warning",
                "anchor-target",
                name,
                line,
                f'In-page link "#{target}" has no matching id in the scanned tree',
                "Add id to the destination, or the smooth-scroll jump silently does nothing",
            )
        )

    if ctx.has_smooth_scroll and not ctx.smooth_scroll_guarded:
        site = ctx.smooth_scroll_sites[0] if ctx.smooth_scroll_sites else ("<project>", 0)
        findings.append(
            Finding(
                "error",
                "reduced-motion",
                site[0],
                site[1],
                "scroll-behavior: smooth is applied unconditionally",
                "Wrap in @media (prefers-reduced-motion: no-preference) - vestibular "
                "disorders affect a large share of adults",
            )
        )

    if ctx.has_smooth_scroll and not ctx.has_scroll_margin:
        site = ctx.smooth_scroll_sites[0] if ctx.smooth_scroll_sites else ("<project>", 0)
        findings.append(
            Finding(
                "warning",
                "scroll-offset",
                site[0],
                site[1],
                "Smooth scroll without scroll-margin-top: a sticky header will cover "
                "the heading you just scrolled to",
                "Set scroll-margin-top on anchor targets to header height + 8px",
            )
        )

    if ctx.has_multi_section_page and not ctx.has_smooth_scroll and ctx.hash_links:
        findings.append(
            Finding(
                "info",
                "scroll-behavior",
                "<project>",
                0,
                "Multi-section page with in-page links but no smooth scrolling",
                "Add guarded scroll-behavior: smooth so jumps read as movement, not teleport",
            )
        )

    if ctx.has_multi_section_page and not (ctx.has_sticky or ctx.has_back_to_top):
        findings.append(
            Finding(
                "warning",
                "return-path",
                "<project>",
                0,
                "Long multi-section page with no sticky nav and no back-to-top control",
                "Give the user a way back without manual scrolling (sticky header or "
                "back-to-top button appearing after ~1 viewport)",
            )
        )

    if ctx.has_multi_section_page and ctx.hash_links and not ctx.has_scroll_spy:
        findings.append(
            Finding(
                "info",
                "wayfinding",
                "<project>",
                0,
                "In-page navigation with no active-section highlighting detected",
                "IntersectionObserver-based scroll spy tells the user where they are",
            )
        )

    if ctx.has_transition_or_animation and not ctx.has_reduced_motion:
        findings.append(
            Finding(
                "error",
                "reduced-motion",
                "<project>",
                0,
                "Animations present but no prefers-reduced-motion handling anywhere",
                "WCAG 2.2 SC 2.3.3: honour the OS reduce-motion setting",
            )
        )

    if not ctx.has_focus_visible:
        findings.append(
            Finding(
                "warning",
                "focus-visible",
                "<project>",
                0,
                "No :focus-visible styling detected in the scanned tree",
                "Keyboard users need a visible focus ring on every interactive element",
            )
        )

    if ctx.has_multi_section_page and not ctx.has_skip_link:
        findings.append(
            Finding(
                "info",
                "skip-link",
                "<project>",
                0,
                "No skip-to-content link detected",
                "Lets keyboard users bypass repeated navigation (WCAG 2.2 SC 2.4.1)",
            )
        )


# ---------------------------------------------------------------------------
# Output
# ---------------------------------------------------------------------------

CSV_FIELDS = ["severity", "check", "file", "line", "message", "hint"]


def sort_findings(findings: list[Finding]) -> list[Finding]:
    """Stable, reproducible ordering: severity, then check, file, line."""

    return sorted(
        findings,
        key=lambda f: (SEVERITY_ORDER.get(f.severity, 9), f.check, f.file, f.line),
    )


def render_text(findings: list[Finding], ctx: ScanContext, counts: dict[str, int]) -> str:
    lines = [
        f"Scanned {ctx.files_scanned} file(s)",
        f"Findings: {counts['error']} error, {counts['warning']} warning, {counts['info']} info",
        "",
    ]
    if not findings:
        lines.append("PASS: no mechanical UX issues found.")
        return "\n".join(lines)
    for finding in findings:
        location = finding.file if finding.line == 0 else f"{finding.file}:{finding.line}"
        lines.append(f"[{finding.severity.upper():<7}] {finding.check:<16} {location}")
        lines.append(f"          {finding.message}")
        if finding.hint:
            lines.append(f"          -> {finding.hint}")
    return "\n".join(lines)


def write_output(path: Path, body: str) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(body, encoding="utf-8", newline="\n")


# ---------------------------------------------------------------------------
# Subcommands
# ---------------------------------------------------------------------------


def cmd_audit(args: argparse.Namespace) -> int:
    root = Path(args.path).expanduser().resolve()
    if not root.exists():
        print(f"Error: path does not exist: {root}", file=sys.stderr)
        print("Pass --path to a source directory or a single file.", file=sys.stderr)
        return 1

    extra_skip = {part.strip() for part in (args.skip or "").split(",") if part.strip()}
    files = iter_files(root, extra_skip)
    if not files:
        print(f"Error: no scannable files under {root}", file=sys.stderr)
        print(
            "Expected one of: " + ", ".join(sorted(SCAN_SUFFIXES)),
            file=sys.stderr,
        )
        return 1

    ctx = ScanContext()
    findings: list[Finding] = []
    base = root if root.is_dir() else root.parent
    for path in files:
        scan_file(path, base, ctx, findings)
    check_project_level(ctx, findings)

    only = {c.strip() for c in (args.only or "").split(",") if c.strip()}
    if only:
        findings = [f for f in findings if f.check in only]

    findings = sort_findings(findings)
    counts = {"error": 0, "warning": 0, "info": 0}
    for finding in findings:
        counts[finding.severity] = counts.get(finding.severity, 0) + 1

    if args.format == "json":
        body = json.dumps(
            {
                "files_scanned": ctx.files_scanned,
                "counts": counts,
                "signals": {
                    "smooth_scroll": ctx.has_smooth_scroll,
                    "smooth_scroll_guarded": ctx.smooth_scroll_guarded,
                    "scroll_margin": ctx.has_scroll_margin,
                    "sticky_header": ctx.has_sticky,
                    "back_to_top": ctx.has_back_to_top,
                    "reduced_motion": ctx.has_reduced_motion,
                    "focus_visible": ctx.has_focus_visible,
                    "skip_link": ctx.has_skip_link,
                    "scroll_spy": ctx.has_scroll_spy,
                },
                "findings": [f.as_row() for f in findings],
            },
            ensure_ascii=False,
            indent=2,
            sort_keys=True,
        )
    elif args.format == "csv":
        import io

        buffer = io.StringIO(newline="")
        writer = csv.DictWriter(buffer, fieldnames=CSV_FIELDS, lineterminator="\n")
        writer.writeheader()
        for finding in findings:
            writer.writerow(finding.as_row())
        body = buffer.getvalue()
    else:
        body = render_text(findings, ctx, counts)

    if args.output and args.dry_run:
        print(f"[dry-run] would write {len(findings)} finding(s) to {args.output}")
        print(f"[dry-run] counts: {counts}")
    elif args.output:
        write_output(Path(args.output).expanduser().resolve(), body + "\n")
        print(f"Wrote {len(findings)} finding(s) to {Path(args.output).resolve()}")
    else:
        print(body)

    gate = SEVERITY_ORDER.get(args.fail_on, None)
    if gate is not None:
        blocking = sum(
            count
            for level, count in counts.items()
            if SEVERITY_ORDER.get(level, 9) <= gate and count
        )
        if blocking:
            return 2
    return 0


def cmd_contrast(args: argparse.Namespace) -> int:
    fg = parse_color(args.fg)
    bg = parse_color(args.bg)
    if fg is None or bg is None:
        print(
            f"Error: could not parse color(s): fg={args.fg!r} bg={args.bg!r}",
            file=sys.stderr,
        )
        print("Use #rgb, #rrggbb, or rgb(r, g, b).", file=sys.stderr)
        return 1

    ratio = contrast_ratio(fg, bg)
    lc = apca_lc(fg, bg)
    result = {
        "foreground": f"#{fg[0]:02x}{fg[1]:02x}{fg[2]:02x}",
        "background": f"#{bg[0]:02x}{bg[1]:02x}{bg[2]:02x}",
        "wcag_ratio": round(ratio, 2),
        "wcag_aa_normal": ratio >= WCAG_AA_NORMAL,
        "wcag_aa_large": ratio >= WCAG_AA_LARGE,
        "wcag_aaa_normal": ratio >= WCAG_AAA_NORMAL,
        "wcag_non_text": ratio >= WCAG_NON_TEXT,
        "apca_lc": round(lc, 1),
        "apca_body_text_ok": abs(lc) >= APCA_BODY_TEXT,
        "apca_large_text_ok": abs(lc) >= APCA_LARGE_TEXT,
        "apca_non_text_ok": abs(lc) >= APCA_NON_TEXT,
    }
    if args.format == "json":
        print(json.dumps(result, ensure_ascii=False, indent=2, sort_keys=True))
    else:
        print(f"{result['foreground']} on {result['background']}")
        print(f"  WCAG 2.2 ratio : {result['wcag_ratio']}:1")
        print(f"    AA body  (4.5:1): {'PASS' if result['wcag_aa_normal'] else 'FAIL'}")
        print(f"    AA large (3:1)  : {'PASS' if result['wcag_aa_large'] else 'FAIL'}")
        print(f"    AAA body (7:1)  : {'PASS' if result['wcag_aaa_normal'] else 'FAIL'}")
        print(f"  APCA Lc        : {result['apca_lc']}  (informative, WCAG 3 draft)")
        print(f"    body  (Lc 75)   : {'PASS' if result['apca_body_text_ok'] else 'FAIL'}")
        print(f"    large (Lc 60)   : {'PASS' if result['apca_large_text_ok'] else 'FAIL'}")
    return 0 if result["wcag_aa_normal"] else 2


def cmd_palette(args: argparse.Namespace) -> int:
    """Check every foreground against every surface in one pass."""

    surfaces = [c.strip() for c in args.surfaces.split(",") if c.strip()]
    foregrounds = [c.strip() for c in args.foregrounds.split(",") if c.strip()]
    if not surfaces or not foregrounds:
        print("Error: --surfaces and --foregrounds must both be non-empty", file=sys.stderr)
        return 1

    rows: list[dict[str, str]] = []
    failures = 0
    for bg_raw in surfaces:
        bg = parse_color(bg_raw)
        if bg is None:
            print(f"Error: could not parse surface color {bg_raw!r}", file=sys.stderr)
            return 1
        for fg_raw in foregrounds:
            fg = parse_color(fg_raw)
            if fg is None:
                print(f"Error: could not parse foreground color {fg_raw!r}", file=sys.stderr)
                return 1
            ratio = contrast_ratio(fg, bg)
            lc = apca_lc(fg, bg)
            verdict = "AA-body" if ratio >= WCAG_AA_NORMAL else (
                "AA-large-only" if ratio >= WCAG_AA_LARGE else "FAIL"
            )
            if verdict == "FAIL":
                failures += 1
            rows.append(
                {
                    "background": f"#{bg[0]:02x}{bg[1]:02x}{bg[2]:02x}",
                    "foreground": f"#{fg[0]:02x}{fg[1]:02x}{fg[2]:02x}",
                    "wcag_ratio": f"{ratio:.2f}",
                    "apca_lc": f"{lc:.0f}",
                    "verdict": verdict,
                }
            )

    rows.sort(key=lambda r: (r["background"], r["foreground"]))
    if args.format == "json":
        print(json.dumps({"failures": failures, "pairs": rows}, ensure_ascii=False, indent=2, sort_keys=True))
    elif args.format == "csv":
        import io

        buffer = io.StringIO(newline="")
        fields = ["background", "foreground", "wcag_ratio", "apca_lc", "verdict"]
        writer = csv.DictWriter(buffer, fieldnames=fields, lineterminator="\n")
        writer.writeheader()
        writer.writerows(rows)
        print(buffer.getvalue(), end="")
    else:
        print(f"{'background':<10} {'foreground':<11} {'WCAG':>7} {'APCA':>6}  verdict")
        for row in rows:
            print(
                f"{row['background']:<10} {row['foreground']:<11} "
                f"{row['wcag_ratio']:>7} {row['apca_lc']:>6}  {row['verdict']}"
            )
        print(f"\n{failures} failing pair(s) out of {len(rows)}")
    return 2 if failures else 0


# ---------------------------------------------------------------------------
# Token file check (assets/tokens.css style: light-dark() + oklch() + var())
# ---------------------------------------------------------------------------

CUSTOM_PROP_RE = re.compile(r"(--[\w-]+)\s*:\s*([^;]+);")
VAR_RE = re.compile(r"var\(\s*(--[\w-]+)\s*(?:,\s*([^()]*))?\)")
CALC_MUL_RE = re.compile(r"calc\(\s*([\d.]+)\s*\*\s*([\d.]+)\s*\)")
BLOCK_RE = re.compile(r"([^{}]+)\{([^{}]*)\}")

# Role pairs every token set must satisfy: (foreground, background, minimum ratio, why).
TOKEN_PAIRS: tuple[tuple[str, str, float, str], ...] = (
    ("--text", "--bg", 4.5, "本文"),
    ("--text", "--surface", 4.5, "カード上の本文"),
    ("--text", "--surface-2", 4.5, "薄い面の本文"),
    ("--text", "--surface-3", 4.5, "hover 面の本文"),
    ("--text-muted", "--bg", 4.5, "補足テキスト"),
    ("--text-muted", "--surface", 4.5, "カード上の補足"),
    ("--text-muted", "--surface-2", 4.5, "薄い面の補足"),
    ("--on-accent", "--accent", 4.5, "主ボタンの文字"),
    ("--on-accent", "--accent-hover", 4.5, "主ボタン hover の文字"),
    ("--accent-text", "--bg", 4.5, "リンク"),
    ("--accent-text", "--surface", 4.5, "カード上のリンク"),
    ("--accent-text", "--accent-soft", 4.5, "アクセント淡色バッジ"),
    ("--success-text", "--success-soft", 4.5, "成功バッジ"),
    ("--warning-text", "--warning-soft", 4.5, "警告バッジ"),
    ("--danger-text", "--danger-soft", 4.5, "エラーバッジ"),
    ("--danger-text", "--surface", 4.5, "エラーメッセージ"),
    ("--on-danger", "--danger", 4.5, "削除ボタンの文字"),
    ("--border-input", "--surface", 3.0, "入力欄の枠 (1.4.11)"),
    ("--focus-ring", "--bg", 3.0, "フォーカスリング"),
    ("--focus-ring", "--surface", 3.0, "カード上のフォーカスリング"),
    ("--success", "--surface", 3.0, "成功アイコン"),
    ("--danger", "--surface", 3.0, "エラーアイコン"),
)


def _split_top_level(args: str) -> list[str]:
    """Split a function argument list on commas that are not nested in parentheses."""

    parts, depth, current = [], 0, []
    for char in args:
        if char == "(":
            depth += 1
        elif char == ")":
            depth -= 1
        if char == "," and depth == 0:
            parts.append("".join(current).strip())
            current = []
            continue
        current.append(char)
    parts.append("".join(current).strip())
    return parts


def _pick_light_dark(value: str, mode: str) -> str:
    """Replace every light-dark(a, b) with a (light) or b (dark)."""

    while True:
        start = value.find("light-dark(")
        if start == -1:
            return value
        depth, index = 0, start + len("light-dark(")
        for index in range(start + len("light-dark("), len(value)):
            if value[index] == "(":
                depth += 1
            elif value[index] == ")":
                if depth == 0:
                    break
                depth -= 1
        inner = value[start + len("light-dark("):index]
        parts = _split_top_level(inner)
        chosen = parts[0] if mode == "light" or len(parts) < 2 else parts[1]
        value = value[:start] + chosen + value[index + 1:]


def load_token_props(css: str, accent: str | None) -> dict[str, str]:
    """Collect :root custom properties, then apply a [data-accent] preset if asked."""

    props: dict[str, str] = {}
    preset: dict[str, str] = {}
    for match in BLOCK_RE.finditer(css):
        selector, body = match.group(1).strip(), match.group(2)
        selector = re.sub(r"/\*.*?\*/", "", selector, flags=re.S).strip()
        body = re.sub(r"/\*.*?\*/", "", body, flags=re.S)
        if selector == ":root":
            props.update({k: v.strip() for k, v in CUSTOM_PROP_RE.findall(body)})
        elif accent and selector == f'[data-accent="{accent}"]':
            preset.update({k: v.strip() for k, v in CUSTOM_PROP_RE.findall(body)})
    if accent and not preset:
        raise ValueError(f'preset [data-accent="{accent}"] not found')
    props.update(preset)
    return props


def resolve_token(name: str, props: dict[str, str], mode: str, depth: int = 0) -> str:
    """Resolve var() references, light-dark() and simple calc() for one token."""

    if depth > 20:
        raise ValueError(f"var() cycle while resolving {name}")
    value = props.get(name)
    if value is None:
        raise KeyError(name)
    value = _pick_light_dark(value, mode)
    value = VAR_RE.sub(lambda m: resolve_token(m.group(1), props, mode, depth + 1), value)
    value = _pick_light_dark(value, mode)
    value = CALC_MUL_RE.sub(lambda m: f"{float(m.group(1)) * float(m.group(2)):.4f}", value)
    return value.strip()


def cmd_tokens(args: argparse.Namespace) -> int:
    """Check the role pairs of a token file in light and dark mode."""

    css = Path(args.file).read_text(encoding="utf-8")
    props = load_token_props(css, args.accent)
    if args.hue is not None:
        props["--brand-h"] = str(args.hue)
    if args.chroma is not None:
        props["--brand-c"] = str(args.chroma)

    rows: list[dict[str, str]] = []
    failures = 0
    for mode in ("light", "dark"):
        for fg_name, bg_name, minimum, why in TOKEN_PAIRS:
            try:
                fg_value = resolve_token(fg_name, props, mode)
                bg_value = resolve_token(bg_name, props, mode)
            except KeyError as missing:
                rows.append({"mode": mode, "pair": f"{fg_name} / {bg_name}", "ratio": "-",
                             "need": f"{minimum}", "verdict": f"SKIP (missing {missing})", "why": why})
                continue
            fg_rgb, bg_rgb = parse_color(fg_value), parse_color(bg_value)
            if fg_rgb is None or bg_rgb is None:
                rows.append({"mode": mode, "pair": f"{fg_name} / {bg_name}", "ratio": "-",
                             "need": f"{minimum}", "verdict": "SKIP (unparsable)", "why": why})
                continue
            ratio = contrast_ratio(fg_rgb, bg_rgb)
            ok = ratio >= minimum
            failures += 0 if ok else 1
            rows.append({"mode": mode, "pair": f"{fg_name} / {bg_name}", "ratio": f"{ratio:.2f}",
                         "need": f"{minimum}", "verdict": "PASS" if ok else "FAIL", "why": why})

    if args.format == "json":
        print(json.dumps({"failures": failures, "pairs": rows}, ensure_ascii=False, indent=2))
    else:
        label = f" accent={args.accent}" if args.accent else ""
        label += f" hue={args.hue}" if args.hue is not None else ""
        print(f"token check: {args.file}{label}")
        for row in rows:
            if args.failures_only and row["verdict"] == "PASS":
                continue
            print(f"  [{row['mode']:<5}] {row['pair']:<34} {row['ratio']:>6} (>= {row['need']})  "
                  f"{row['verdict']}  {row['why']}")
        print(f"\n{failures} failing pair(s) out of {len(rows)}")
    return 2 if failures else 0


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="ux_audit.py",
        description="Deterministic UI/UX audit: contrast, scroll UX, navigation, a11y, animation cost",
    )
    subparsers = parser.add_subparsers(dest="command", required=True)

    audit = subparsers.add_parser("audit", help="Scan a frontend source tree")
    audit.add_argument("--path", required=True, help="Directory or file to scan")
    audit.add_argument("--format", choices=["text", "json", "csv"], default="text")
    audit.add_argument("--output", help="Write the report to this path instead of stdout")
    audit.add_argument("--only", help="Comma-separated check names to keep")
    audit.add_argument("--skip", help="Comma-separated extra directory names to skip")
    audit.add_argument(
        "--fail-on",
        choices=["error", "warning", "info"],
        help="Exit with code 2 when findings at or above this severity exist",
    )
    audit.add_argument("--dry-run", action="store_true", help="Do not write --output")
    audit.set_defaults(func=cmd_audit)

    contrast = subparsers.add_parser("contrast", help="Check one foreground/background pair")
    contrast.add_argument("--fg", required=True, help="Foreground color (#rrggbb or rgb())")
    contrast.add_argument("--bg", required=True, help="Background color (#rrggbb or rgb())")
    contrast.add_argument("--format", choices=["text", "json"], default="text")
    contrast.set_defaults(func=cmd_contrast)

    palette = subparsers.add_parser("palette", help="Check a full palette matrix at once")
    palette.add_argument("--surfaces", required=True, help="Comma-separated background colors")
    palette.add_argument("--foregrounds", required=True, help="Comma-separated foreground colors")
    palette.add_argument("--format", choices=["text", "json", "csv"], default="text")
    palette.set_defaults(func=cmd_palette)

    tokens = subparsers.add_parser(
        "tokens", help="Check role pairs of a tokens.css (light-dark/oklch/var) in both modes"
    )
    tokens.add_argument("--file", required=True, help="Path to tokens.css")
    tokens.add_argument("--accent", help='Apply a [data-accent="..."] preset before checking')
    tokens.add_argument("--hue", type=float, help="Override --brand-h (0-360)")
    tokens.add_argument("--chroma", type=float, help="Override --brand-c")
    tokens.add_argument("--failures-only", action="store_true", help="Print only failing pairs")
    tokens.add_argument("--format", choices=["text", "json"], default="text")
    tokens.set_defaults(func=cmd_tokens)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    return args.func(args)


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except KeyboardInterrupt:
        print("Aborted.", file=sys.stderr)
        raise SystemExit(130)
    except Exception as exc:  # noqa: BLE001 - surface a short, actionable message
        print(f"Error: {exc}", file=sys.stderr)
        print("Re-run with --help to check argument usage.", file=sys.stderr)
        raise SystemExit(1)
