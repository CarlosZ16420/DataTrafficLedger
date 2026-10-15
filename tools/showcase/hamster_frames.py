"""README 仓库、归档工作台、项目详情与相似度功能展示图工程。

修改下面的“布局参数”，然后运行本文件即可重新生成中英文图片。
仓库和工作台保留完整截图，以原始分辨率放入圆角窗口。
项目详情只裁掉截屏边缘，将完整功能区域重新放入统一卡片。
保留圆角卡片和柔和阴影，截图外部及卡片间隙为透明背景。
"""

import argparse
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter


PROJECT_ROOT = Path(__file__).resolve().parent
SOURCE_ROOT = PROJECT_ROOT / "源图"
PREVIEW_ROOT = PROJECT_ROOT / "输出"

# 布局参数：可直接修改这些数值和颜色。
CANVAS_WIDTH = 1920
CANVAS_HEIGHT = 1180
OUTER_MARGIN = 36
COLUMN_GAP = 30
SECTION_GAP = 22
CARD_PADDING = 12
CARD_RADIUS = 16
CARD_COLOR = "#ffffff"
CARD_BORDER_COLOR = "#ddd9d0"
SHADOW_COLOR = (38, 34, 28, 46)
SHADOW_BLUR = 11

# 仓库与归档工作台：保留原有圆角外框、阴影和留白。
WINDOW_MARGIN = 72
WINDOW_PADDING = 12
WINDOW_RADIUS = 24
SCREENSHOT_RADIUS = 12
WINDOW_SOURCES = {"library": "仓库", "archive": "归档工作台"}
SIMILARITY_LEFT_WIDTH = 1100

# 原始截图中的功能区域。坐标仅排除截屏时残留的页面边线。
SECTION_CROPS = {
    "header": (20, 20, 875, 176),
    "details": (22, 199, 873, 787),
    "similar": (22, 811, 873, 907),
    "media": (21, 3, 873, 627),
    "directory": (20, 648, 874, 999),
}


def fit_width(image: Image.Image, width: int) -> Image.Image:
    """按比例缩放到指定宽度。"""
    scale = width / image.width
    size = (width, round(image.height * scale))
    return image.resize(size, Image.Resampling.LANCZOS)


def make_background(width: int = CANVAS_WIDTH, height: int = CANVAS_HEIGHT) -> Image.Image:
    return Image.new("RGBA", (width, height), (0, 0, 0, 0))


def prepare_section(screenshot: Image.Image, crop_name: str, card_width: int) -> tuple[Image.Image, int]:
    """提取功能区并计算统一外框高度。"""
    cropped = screenshot.crop(SECTION_CROPS[crop_name])
    fitted = fit_width(cropped, card_width - CARD_PADDING * 2)
    return fitted, fitted.height + CARD_PADDING * 2


def add_section(canvas: Image.Image, fitted: Image.Image, x: int, y: int, card_width: int, card_height: int) -> None:
    """保留原有圆角卡片，以透明画布承载柔和阴影。"""
    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (x + 4, y + 8, x + card_width + 4, y + card_height + 8),
        radius=CARD_RADIUS + 2, fill=SHADOW_COLOR,
    )
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(SHADOW_BLUR)))
    ImageDraw.Draw(canvas).rounded_rectangle(
        (x, y, x + card_width, y + card_height),
        radius=CARD_RADIUS, fill=CARD_COLOR, outline=CARD_BORDER_COLOR, width=1,
    )
    canvas.paste(fitted, (x + CARD_PADDING, y + CARD_PADDING))


def add_column(canvas: Image.Image, screenshot_map: dict[str, Image.Image], names: tuple[str, ...], x: int, card_width: int) -> None:
    prepared = [prepare_section(screenshot_map[name], name, card_width) for name in names]
    total_height = sum(height for _, height in prepared) + SECTION_GAP * (len(prepared) - 1)
    y = max(OUTER_MARGIN, (CANVAS_HEIGHT - total_height) // 2)
    for fitted, height in prepared:
        add_section(canvas, fitted, x, y, card_width, height)
        y += height + SECTION_GAP


def save_showcase(canvas: Image.Image, filename: str, readme_root: Path | None) -> Path:
    PREVIEW_ROOT.mkdir(parents=True, exist_ok=True)
    preview_path = PREVIEW_ROOT / filename
    canvas.save(preview_path, optimize=True)
    if readme_root is not None:
        asset_root = readme_root / "assets" / "readme"
        asset_root.mkdir(parents=True, exist_ok=True)
        (asset_root / filename).write_bytes(preview_path.read_bytes())
    return preview_path


def build(locale: str, readme_root: Path | None = None) -> Path:
    first = Image.open(SOURCE_ROOT / f"项目详情1_{locale}.png").convert("RGB")
    second = Image.open(SOURCE_ROOT / f"项目详情2_{locale}.png").convert("RGB")

    card_width = (CANVAS_WIDTH - OUTER_MARGIN * 2 - COLUMN_GAP) // 2
    canvas = make_background()
    screenshots = {
        "header": first,
        "details": first,
        "similar": first,
        "media": second,
        "directory": second,
    }
    add_column(canvas, screenshots, ("header", "details", "similar"), OUTER_MARGIN, card_width)
    add_column(
        canvas,
        screenshots,
        ("media", "directory"),
        OUTER_MARGIN + card_width + COLUMN_GAP,
        card_width,
    )

    suffix = "zh-CN" if locale == "zh" else "en-US"
    return save_showcase(canvas, f"details-showcase.{suffix}.png", readme_root)


def rounded_mask(size: tuple[int, int], radius: int) -> Image.Image:
    """只对边缘蒙版抗锯齿，不缩放截图文字。"""
    scale = 4
    mask = Image.new("L", (size[0] * scale, size[1] * scale), 0)
    ImageDraw.Draw(mask).rounded_rectangle(
        (0, 0, mask.width - 1, mask.height - 1), radius=radius * scale, fill=255,
    )
    return mask.resize(size, Image.Resampling.LANCZOS)


def build_window(section: str, locale: str, readme_root: Path | None = None) -> Path:
    source_path = SOURCE_ROOT / f"{WINDOW_SOURCES[section]}_{locale}.png"
    with Image.open(source_path) as source:
        screenshot = source.convert("RGB")
    card_size = (screenshot.width + WINDOW_PADDING * 2, screenshot.height + WINDOW_PADDING * 2)
    canvas = make_background(card_size[0] + WINDOW_MARGIN * 2, card_size[1] + WINDOW_MARGIN * 2)
    x = y = WINDOW_MARGIN

    shadow = Image.new("RGBA", canvas.size, (0, 0, 0, 0))
    ImageDraw.Draw(shadow).rounded_rectangle(
        (x + 4, y + 8, x + card_size[0] + 3, y + card_size[1] + 7),
        radius=WINDOW_RADIUS, fill=SHADOW_COLOR,
    )
    canvas.alpha_composite(shadow.filter(ImageFilter.GaussianBlur(SHADOW_BLUR)))

    card = Image.new("RGB", card_size, CARD_COLOR)
    ImageDraw.Draw(card).rounded_rectangle(
        (0, 0, card.width - 1, card.height - 1),
        radius=WINDOW_RADIUS, outline=CARD_BORDER_COLOR, width=1,
    )
    card.paste(screenshot, (WINDOW_PADDING, WINDOW_PADDING), rounded_mask(screenshot.size, SCREENSHOT_RADIUS))
    layer = card.convert("RGBA")
    layer.putalpha(rounded_mask(card_size, WINDOW_RADIUS))
    canvas.alpha_composite(layer, (x, y))

    suffix = "zh-CN" if locale == "zh" else "en-US"
    return save_showcase(canvas, f"{section}-showcase.{suffix}.png", readme_root)


def build_similarity(locale: str, readme_root: Path | None = None) -> Path:
    """按队列、报告、白名单确认和设置的顺序展示真实界面。"""
    right_width = CANVAS_WIDTH - OUTER_MARGIN * 2 - COLUMN_GAP - SIMILARITY_LEFT_WIDTH
    def prepare(name: str, width: int) -> tuple[Image.Image, int]:
        with Image.open(SOURCE_ROOT / f"{name}_{locale}.png") as source:
            panel = Image.new("RGBA", source.size, CARD_COLOR)
            panel.alpha_composite(source.convert("RGBA"))
            fitted = fit_width(panel.convert("RGB"), width - CARD_PADDING * 2)
        return fitted, fitted.height + CARD_PADDING * 2

    queue_width = CANVAS_WIDTH - OUTER_MARGIN * 2
    queue = prepare("队列相似入口", queue_width)
    report = prepare("相似报告含白名单操作", SIMILARITY_LEFT_WIDTH)
    whitelist = prepare("白名单确认", right_width)
    settings = prepare("相似度设置", right_width)
    body_y = OUTER_MARGIN + queue[1] + SECTION_GAP
    height = body_y + max(report[1], whitelist[1] + SECTION_GAP + settings[1]) + OUTER_MARGIN
    canvas = make_background(CANVAS_WIDTH, height)
    right_x = OUTER_MARGIN + SIMILARITY_LEFT_WIDTH + COLUMN_GAP
    add_section(canvas, queue[0], OUTER_MARGIN, OUTER_MARGIN, queue_width, queue[1])
    add_section(canvas, report[0], OUTER_MARGIN, body_y, SIMILARITY_LEFT_WIDTH, report[1])
    add_section(canvas, whitelist[0], right_x, body_y, right_width, whitelist[1])
    add_section(canvas, settings[0], right_x, body_y + whitelist[1] + SECTION_GAP, right_width, settings[1])
    suffix = "zh-CN" if locale == "zh" else "en-US"
    return save_showcase(canvas, f"similarity-showcase.{suffix}.png", readme_root)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--section", choices=("all", "library", "archive", "details", "similarity"), default="all",
                        help="选择生成的展示图，默认全部生成。")
    parser.add_argument("--readme-root", type=Path,
                        help="同时更新指定仓库的 assets/readme；省略时只生成本地输出。")
    args = parser.parse_args()
    if args.readme_root is not None and not (args.readme_root / "README.md").is_file():
        parser.error("--readme-root 必须指向包含 README.md 的源码仓库。")
    for language in ("zh", "en"):
        for section in ("library", "archive", "details", "similarity"):
            if args.section not in ("all", section):
                continue
            if section == "details":
                result = build(language, args.readme_root)
            elif section == "similarity":
                result = build_similarity(language, args.readme_root)
            else:
                result = build_window(section, language, args.readme_root)
            print(f"已生成：{result}")
