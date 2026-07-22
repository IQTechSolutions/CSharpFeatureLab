from __future__ import annotations

import argparse
import asyncio
import hashlib
import json
import math
import re
import shutil
import subprocess
import sys
import textwrap
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


WIDE = (1920, 1080)
VERTICAL = (1080, 1920)
FPS = 30
VOICE = "en-GB-RyanNeural"
FONT_REGULAR = "C:/Windows/Fonts/segoeui.ttf"
FONT_SEMIBOLD = "C:/Windows/Fonts/seguisb.ttf"
FONT_BOLD = "C:/Windows/Fonts/segoeuib.ttf"
FONT_MONO = "C:/Windows/Fonts/consola.ttf"
FONT_MONO_BOLD = "C:/Windows/Fonts/consolab.ttf"
INK = "#07111F"
INK_2 = "#0B1B2E"
PANEL = "#10243A"
PANEL_2 = "#132B45"
WHITE = "#F8FAFC"
MUTED = "#A9B8CA"
CYAN = "#22D3EE"
TEAL = "#2DD4BF"
PURPLE = "#8B5CF6"
AMBER = "#F59E0B"
GREEN = "#34D399"
RED = "#FB7185"


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Render C# Feature Lab pilot episodes")
    parser.add_argument("--content", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--edge-tts-deps", type=Path, required=True)
    return parser.parse_args()


def run(command: list[str], cwd: Path | None = None) -> None:
    subprocess.run(command, cwd=cwd, check=True)


def probe(path: Path) -> dict:
    result = subprocess.run(
        ["ffprobe", "-v", "error", "-show_streams", "-show_format", "-of", "json", str(path)],
        check=True,
        capture_output=True,
        text=True,
    )
    return json.loads(result.stdout)


def duration(path: Path) -> float:
    return float(probe(path)["format"]["duration"])


def sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def font(size: int, *, bold: bool = False, semi: bool = False, mono: bool = False) -> ImageFont.FreeTypeFont:
    if mono:
        path = FONT_MONO_BOLD if bold else FONT_MONO
    else:
        path = FONT_BOLD if bold else FONT_SEMIBOLD if semi else FONT_REGULAR
    return ImageFont.truetype(path, size)


def rgb(value: str) -> tuple[int, int, int]:
    return tuple(int(value[index:index + 2], 16) for index in (1, 3, 5))


def gradient(size: tuple[int, int], accent: str) -> Image.Image:
    width, height = size
    image = Image.new("RGB", size)
    draw = ImageDraw.Draw(image)
    start, end = rgb(INK), rgb(INK_2)
    for y in range(height):
        ratio = y / max(1, height - 1)
        color = tuple(round(start[i] * (1 - ratio) + end[i] * ratio) for i in range(3))
        draw.line((0, y, width, y), fill=color)
    glow = Image.new("RGBA", size, (0, 0, 0, 0))
    glow_draw = ImageDraw.Draw(glow)
    glow_draw.ellipse((-width * .18, -height * .35, width * .48, height * .75), fill=accent + "35")
    glow_draw.ellipse((width * .72, height * .55, width * 1.15, height * 1.2), fill=PURPLE + "22")
    glow = glow.filter(ImageFilter.GaussianBlur(max(60, width // 14)))
    image.paste(glow, (0, 0), glow)
    return image


def wrapped_lines(text: str, width: int) -> str:
    return "\n".join(textwrap.wrap(text, width=width, break_long_words=False, break_on_hyphens=False))


def draw_brand(draw: ImageDraw.ImageDraw, size: tuple[int, int], accent: str, episode_number: int, scene_number: int) -> None:
    width, height = size
    draw.rounded_rectangle((64, 48, 430, 108), radius=30, fill="#102B3B", outline="#2A6075", width=2)
    draw.ellipse((87, 70, 105, 88), fill=accent)
    draw.text((126, 63), "C# FEATURE LAB", fill=WHITE, font=font(24, semi=True))
    draw.text((width - 64, 65), f"EP {episode_number:02}  /  {scene_number:02}", fill=MUTED, font=font(23, semi=True), anchor="ra")
    draw.line((64, height - 66, width - 64, height - 66), fill="#284059", width=2)
    draw.text((64, height - 50), "REAL .NET FEATURES IN UNDER TEN MINUTES", fill=MUTED, font=font(20, semi=True))


def draw_code(draw: ImageDraw.ImageDraw, code: str, bounds: tuple[int, int, int, int], accent: str) -> None:
    x1, y1, x2, y2 = bounds
    draw.rounded_rectangle(bounds, radius=28, fill="#06101D", outline="#2B4865", width=3)
    draw.rounded_rectangle((x1 + 22, y1 + 20, x1 + 190, y1 + 62), radius=21, fill=PANEL_2)
    draw.ellipse((x1 + 39, y1 + 34, x1 + 51, y1 + 46), fill=RED)
    draw.ellipse((x1 + 61, y1 + 34, x1 + 73, y1 + 46), fill=AMBER)
    draw.ellipse((x1 + 83, y1 + 34, x1 + 95, y1 + 46), fill=GREEN)
    draw.text((x1 + 112, y1 + 27), "C#", fill=accent, font=font(19, bold=True, mono=True))
    lines = code.strip("\n").splitlines()
    code_size = 31 if len(lines) <= 10 else 27
    line_height = code_size + 13
    y = y1 + 86
    for number, line in enumerate(lines, 1):
        draw.text((x1 + 28, y), f"{number:>2}", fill="#57718B", font=font(code_size - 3, mono=True))
        color = WHITE
        stripped = line.strip()
        if stripped.startswith("//"):
            color = MUTED
        elif any(token in stripped for token in ("public ", "private ", "await ", "return ", "var ", "class ", "record ")):
            color = "#D8B4FE"
        draw.text((x1 + 88, y), line.expandtabs(4), fill=color, font=font(code_size, mono=True))
        y += line_height
        if y > y2 - line_height:
            break


def render_wide_scene(episode: dict, scene: dict, scene_number: int, path: Path) -> None:
    accent = episode.get("accent", CYAN)
    image = gradient(WIDE, accent)
    draw = ImageDraw.Draw(image)
    draw_brand(draw, WIDE, accent, episode["number"], scene_number)
    draw.text((78, 146), scene["eyebrow"].upper(), fill=accent, font=font(25, semi=True))
    has_code = bool(scene.get("code"))
    title = wrapped_lines(scene["title"], 25 if has_code else 34)
    draw.multiline_text((78, 194), title, fill=WHITE, font=font(49 if has_code else 54, bold=True), spacing=8)

    if has_code:
        bullets = scene.get("bullets", [])
        y = 390
        for bullet in bullets:
            draw.ellipse((88, y + 12, 104, y + 28), fill=accent)
            draw.multiline_text((126, y), wrapped_lines(bullet, 34), fill=MUTED, font=font(29, semi=True), spacing=7)
            y += 105
        draw_code(draw, scene["code"], (780, 142, 1840, 985), accent)
    else:
        bullets = scene.get("bullets", [])
        y = 420
        for bullet in bullets:
            draw.rounded_rectangle((92, y - 12, 1828, y + 112), radius=26, fill=PANEL, outline="#254866", width=2)
            draw.ellipse((126, y + 28, 152, y + 54), fill=accent)
            draw.multiline_text((184, y + 9), wrapped_lines(bullet, 70), fill=WHITE, font=font(34, semi=True), spacing=7)
            y += 151
    image.save(path, quality=96)


def render_thumbnail(episode: dict, path: Path) -> None:
    accent = episode.get("accent", CYAN)
    image = gradient((1280, 720), accent)
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((54, 46, 375, 104), radius=29, fill="#102B3B", outline="#2A6075", width=2)
    draw.text((82, 60), f"C# FEATURE LAB  {episode['number']:02}", fill=WHITE, font=font(24, semi=True))
    draw.rounded_rectangle((825, 80, 1194, 640), radius=42, fill="#06101D", outline=accent, width=5)
    for index, line in enumerate(episode["thumbnailCode"]):
        draw.text((860, 132 + index * 65), line, fill=WHITE if index % 2 else "#D8B4FE", font=font(29, mono=True))
    title = wrapped_lines(episode["thumbnail"], 17)
    draw.multiline_text((66, 194), title, fill=WHITE, font=font(72, bold=True), spacing=4)
    draw.rounded_rectangle((67, 595, 692, 658), radius=31, fill=accent)
    draw.text((379, 626), episode["playlist"].upper(), anchor="mm", fill=INK, font=font(26, bold=True))
    image.save(path, quality=96)


def render_vertical_scene(episode: dict, scene: dict, scene_number: int, path: Path) -> None:
    accent = episode.get("accent", CYAN)
    image = gradient(VERTICAL, accent)
    draw = ImageDraw.Draw(image)
    draw.rounded_rectangle((66, 70, 548, 139), radius=34, fill="#102B3B", outline="#2A6075", width=2)
    draw.text((102, 88), "C# FEATURE LAB", fill=WHITE, font=font(27, semi=True))
    draw.text((1010, 93), f"{scene_number:02}", fill=MUTED, font=font(26, semi=True), anchor="ra")
    draw.text((72, 280), scene["eyebrow"].upper(), fill=accent, font=font(31, semi=True))
    draw.multiline_text((72, 352), wrapped_lines(scene["title"], 16), fill=WHITE, font=font(76, bold=True), spacing=8)
    y = 950
    for bullet in scene.get("bullets", []):
        draw.rounded_rectangle((72, y, 1008, y + 180), radius=38, fill=PANEL, outline="#254866", width=3)
        draw.ellipse((112, y + 68, 140, y + 96), fill=accent)
        draw.multiline_text((176, y + 38), wrapped_lines(bullet, 30), fill=WHITE, font=font(38, semi=True), spacing=8)
        y += 220
    draw.line((72, 1810, 1008, 1810), fill="#284059", width=2)
    draw.text((72, 1840), "WATCH THE FULL LESSON", fill=MUTED, font=font(25, semi=True))
    image.save(path, quality=96)


async def synthesize_segments(edge_tts, texts: list[str], audio_dir: Path, prefix: str) -> tuple[list[Path], list[dict], list[tuple[float, float]]]:
    audio_dir.mkdir(parents=True, exist_ok=True)
    audio_files: list[Path] = []
    all_boundaries: list[dict] = []
    ranges: list[tuple[float, float]] = []
    offset = 0.0
    for index, text in enumerate(texts, 1):
        audio_path = audio_dir / f"{prefix}-{index:02}.mp3"
        local_boundaries: list[dict] = []
        communicator = edge_tts.Communicate(text, VOICE, rate="-4%", volume="+0%", pitch="-2Hz", boundary="WordBoundary")
        with audio_path.open("wb") as stream:
            async for chunk in communicator.stream():
                if chunk["type"] == "audio":
                    stream.write(chunk["data"])
                elif chunk["type"] == "WordBoundary":
                    local_boundaries.append({
                        "text": chunk["text"],
                        "start": offset + chunk["offset"] / 10_000_000,
                        "duration": chunk["duration"] / 10_000_000,
                    })
        segment_duration = duration(audio_path)
        ranges.append((offset, offset + segment_duration))
        all_boundaries.extend(local_boundaries)
        audio_files.append(audio_path)
        offset += segment_duration
    return audio_files, all_boundaries, ranges


def timestamp_srt(seconds: float) -> str:
    milliseconds = round(seconds * 1000)
    hours, remainder = divmod(milliseconds, 3_600_000)
    minutes, remainder = divmod(remainder, 60_000)
    secs, millis = divmod(remainder, 1000)
    return f"{hours:02}:{minutes:02}:{secs:02},{millis:03}"


def write_srt(boundaries: list[dict], total_duration: float, path: Path, words_per_caption: int = 9) -> None:
    blocks = []
    index = 1
    for start_index in range(0, len(boundaries), words_per_caption):
        chunk = boundaries[start_index:start_index + words_per_caption]
        start = max(0.0, chunk[0]["start"] - .03)
        if start_index + words_per_caption < len(boundaries):
            end = max(start + .25, boundaries[start_index + words_per_caption]["start"] - .05)
        else:
            end = total_duration
        text = " ".join(item["text"] for item in chunk)
        blocks.append(f"{index}\n{timestamp_srt(start)} --> {timestamp_srt(end)}\n{text}\n")
        index += 1
    path.write_text("\n".join(blocks), encoding="utf-8")


def concat_audio(audio_files: list[Path], output: Path, build_dir: Path, name: str) -> None:
    concat_file = build_dir / f"{name}-audio-concat.txt"
    concat_file.write_text("\n".join(f"file '{path.as_posix()}'" for path in audio_files), encoding="utf-8")
    run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", str(concat_file), "-c:a", "libmp3lame", "-b:a", "192k", str(output)])


def render_video_from_scenes(scene_files: list[Path], ranges: list[tuple[float, float]], size: tuple[int, int], output: Path, build_dir: Path, name: str) -> None:
    clips: list[Path] = []
    width, height = size
    for index, (scene_path, (start, end)) in enumerate(zip(scene_files, ranges, strict=True), 1):
        clip = build_dir / f"{name}-clip-{index:02}.mp4"
        clip_duration = end - start
        frames = max(1, round(clip_duration * FPS))
        zoom = f"zoompan=z='min(zoom+0.00010,1.018)':d={frames}:s={width}x{height}:fps={FPS},format=yuv420p"
        run([
            "ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-loop", "1", "-i", str(scene_path),
            "-vf", zoom, "-t", f"{clip_duration:.4f}", "-an", "-c:v", "libx264", "-preset", "veryfast",
            "-crf", "20", "-pix_fmt", "yuv420p", str(clip),
        ])
        clips.append(clip)
    concat_file = build_dir / f"{name}-video-concat.txt"
    concat_file.write_text("\n".join(f"file '{path.as_posix()}'" for path in clips), encoding="utf-8")
    run(["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", str(concat_file), "-c", "copy", str(output)])


def combine(video: Path, audio: Path, output: Path, captions: Path | None = None) -> None:
    command = ["ffmpeg", "-y", "-hide_banner", "-loglevel", "error", "-i", str(video), "-i", str(audio)]
    if captions:
        escaped = str(captions).replace("\\", "/").replace(":", "\\:")
        command.extend(["-vf", f"subtitles='{escaped}':force_style='FontName=Segoe UI Semibold,FontSize=22,PrimaryColour=&H00FFFFFF,OutlineColour=&H00111B2A,BorderStyle=1,Outline=3,Shadow=0,MarginV=70'"])
    command.extend([
        "-c:v", "libx264", "-preset", "veryfast", "-crf", "19", "-pix_fmt", "yuv420p",
        "-af", "volume=4.5dB", "-c:a", "aac", "-b:a", "192k", "-ar", "48000", "-ac", "2", "-shortest", str(output),
    ])
    run(command)


def chapter_text(episode: dict, ranges: list[tuple[float, float]]) -> str:
    lines = []
    for chapter in episode["chapters"]:
        start = ranges[chapter["scene"] - 1][0]
        minutes, seconds = divmod(round(start), 60)
        lines.append(f"{minutes:02}:{seconds:02} {chapter['title']}")
    return "\n".join(lines)


def validate_media(path: Path, width: int, height: int, minimum: float, maximum: float) -> dict:
    info = probe(path)
    video = next(stream for stream in info["streams"] if stream["codec_type"] == "video")
    audio = next(stream for stream in info["streams"] if stream["codec_type"] == "audio")
    media_duration = float(info["format"]["duration"])
    checks = {
        "width": int(video["width"]) == width,
        "height": int(video["height"]) == height,
        "videoCodec": video["codec_name"] == "h264",
        "audioCodec": audio["codec_name"] == "aac",
        "audioSampleRate": int(audio["sample_rate"]) == 48000,
        "duration": minimum <= media_duration <= maximum,
    }
    if not all(checks.values()):
        raise RuntimeError(f"Media validation failed for {path}: {checks}")
    return {"durationSeconds": media_duration, "checks": checks, "sha256": sha256(path)}


def make_contact_sheet(scene_files: list[Path], output: Path) -> None:
    thumbs = [Image.open(path).resize((480, 270)) for path in scene_files]
    columns = 3
    rows = math.ceil(len(thumbs) / columns)
    sheet = Image.new("RGB", (columns * 480, rows * 270), INK)
    for index, thumb in enumerate(thumbs):
        sheet.paste(thumb, ((index % columns) * 480, (index // columns) * 270))
    sheet.save(output, quality=94)


async def render_episode(edge_tts, episode: dict, output_root: Path) -> dict:
    episode_dir = output_root / f"{episode['number']:02}-{episode['slug']}"
    assets = episode_dir / "assets"
    audio_dir = episode_dir / "audio"
    build = episode_dir / "build"
    for directory in (episode_dir, assets, audio_dir, build):
        directory.mkdir(parents=True, exist_ok=True)

    scene_files = []
    for index, scene in enumerate(episode["scenes"], 1):
        scene_path = assets / f"scene-{index:02}.png"
        render_wide_scene(episode, scene, index, scene_path)
        scene_files.append(scene_path)
    thumbnail = episode_dir / "thumbnail-1280x720.png"
    render_thumbnail(episode, thumbnail)
    contact_sheet = episode_dir / "contact-sheet.png"
    make_contact_sheet(scene_files, contact_sheet)

    narration_texts = [scene["narration"] for scene in episode["scenes"]]
    audio_files, boundaries, ranges = await synthesize_segments(edge_tts, narration_texts, audio_dir, "lesson")
    full_audio = build / "lesson-audio.mp3"
    concat_audio(audio_files, full_audio, build, "lesson")
    captions = episode_dir / "captions.srt"
    write_srt(boundaries, duration(full_audio), captions)
    raw_video = build / "lesson-video.mp4"
    render_video_from_scenes(scene_files, ranges, WIDE, raw_video, build, "lesson")
    lesson = episode_dir / f"{episode['slug']}.mp4"
    combine(raw_video, full_audio, lesson)

    transcript = "# Transcript\n\n" + "\n\n".join(
        f"## {scene['title']}\n\n{scene['narration']}" for scene in episode["scenes"]
    ) + "\n"
    (episode_dir / "transcript.md").write_text(transcript, encoding="utf-8")
    chapters = chapter_text(episode, ranges)
    description = f"{episode['description']}\n\nChapters\n{chapters}\n\n{episode['footer']}"
    metadata = {
        "title": episode["title"],
        "description": description,
        "category": "Education",
        "educationType": "How-to",
        "playlist": episode["playlist"],
        "visibility": "private",
        "tags": episode["tags"],
        "chapters": chapters,
    }
    (episode_dir / "youtube-metadata.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
    (episode_dir / "linkedin.md").write_text(episode["linkedin"].strip() + "\n", encoding="utf-8")
    (episode_dir / "dev-article.md").write_text(episode["devArticle"].strip() + "\n", encoding="utf-8")

    short_scene_files = []
    for index, scene in enumerate(episode["short"]["scenes"], 1):
        scene_path = assets / f"short-scene-{index:02}.png"
        render_vertical_scene(episode, scene, index, scene_path)
        short_scene_files.append(scene_path)
    short_audio_files, short_boundaries, short_ranges = await synthesize_segments(
        edge_tts, [scene["narration"] for scene in episode["short"]["scenes"]], audio_dir, "short"
    )
    short_audio = build / "short-audio.mp3"
    concat_audio(short_audio_files, short_audio, build, "short")
    short_captions = episode_dir / "short-captions.srt"
    write_srt(short_boundaries, duration(short_audio), short_captions, words_per_caption=6)
    short_raw = build / "short-video.mp4"
    render_video_from_scenes(short_scene_files, short_ranges, VERTICAL, short_raw, build, "short")
    short_video = episode_dir / f"{episode['slug']}-short.mp4"
    combine(short_raw, short_audio, short_video, short_captions)
    short_cover = episode_dir / "short-cover-1080x1920.png"
    render_vertical_scene(episode, episode["short"]["scenes"][0], 1, short_cover)

    lesson_validation = validate_media(lesson, 1920, 1080, 360, 600)
    short_validation = validate_media(short_video, 1080, 1920, 40, 60)
    manifest = {
        "episode": episode["number"],
        "checkpoint": episode["checkpoint"],
        "voice": VOICE,
        "lesson": lesson_validation,
        "short": short_validation,
        "files": {
            "lesson": str(lesson),
            "short": str(short_video),
            "thumbnail": str(thumbnail),
            "captions": str(captions),
            "transcript": str(episode_dir / "transcript.md"),
            "metadata": str(episode_dir / "youtube-metadata.json"),
            "contactSheet": str(contact_sheet),
        },
    }
    (episode_dir / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest


async def main() -> None:
    args = parse_args()
    if not shutil.which("ffmpeg") or not shutil.which("ffprobe"):
        raise RuntimeError("ffmpeg and ffprobe are required")
    sys.path.insert(0, str(args.edge_tts_deps.resolve()))
    import edge_tts  # noqa: PLC0415

    data = json.loads(args.content.read_text(encoding="utf-8"))
    args.output.mkdir(parents=True, exist_ok=True)
    manifests = []
    for episode in data["episodes"]:
        manifests.append(await render_episode(edge_tts, episode, args.output))
    summary = {"voice": VOICE, "episodes": manifests}
    (args.output / "pilot-production-summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


if __name__ == "__main__":
    asyncio.run(main())
