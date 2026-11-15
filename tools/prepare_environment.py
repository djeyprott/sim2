"""Prepare climate, lake and river inputs for the Earth import tool.

Reads:
  TerraClimate 1981-2010 monthly climatology (tmax, tmin, ppt) as netCDF
  Natural Earth 10m lakes and river centrelines as shapefiles
Writes, all on a 4320 x 2160 grid (1/12 degree), rows north to south, columns 180 W to 180 E:
  climate_tmean.i16  12 months of mean temperature in tenths of a degree C, -32768 where unknown
  climate_ppt.i16    12 months of rainfall in mm, -32768 where unknown
  lakes.u8           1 where a natural lake covers the pixel
  rivers.u8          0 none, 1 minor river, 2 major river
and a file of river lines for the viewer to draw.

Usage: python prepare_environment.py SOURCE_DIR OUTPUT_DIR RIVER_LINES_FILE
"""
import collections
import struct
import sys
import warnings

import netCDF4
import numpy as np
import shapefile
from PIL import Image, ImageChops, ImageDraw

WIDTH, HEIGHT = 4320, 2160
NODATA = -32768

# Natural Earth "scalerank": lower means a more important river.
MAJOR_RIVER_MAX_RANK = 5
# Ranks 8 and above are regional extras that cover only North America and Europe,
# so they are left out to keep every continent on the same footing.
MINOR_RIVER_MAX_RANK = 7

src, out, river_lines_path = sys.argv[1], sys.argv[2], sys.argv[3]


def read_month(dataset, name, month):
    var = dataset.variables[name]
    data = var[month, :, :]
    data = np.ma.filled(np.ma.asarray(data, dtype=np.float32), np.nan)
    return data


def downsample(data):
    h, w = data.shape
    blocks = data.reshape(HEIGHT, h // HEIGHT, WIDTH, w // WIDTH)
    with warnings.catch_warnings():
        warnings.simplefilter("ignore", category=RuntimeWarning)
        return np.nanmean(blocks, axis=(1, 3))


def check_grid(dataset, name):
    var = dataset.variables[name]
    print(name, "dims", var.dimensions, "shape", var.shape)
    lat = dataset.variables["lat"][:]
    lon = dataset.variables["lon"][:]
    print("  lat", float(lat[0]), "to", float(lat[-1]), "lon", float(lon[0]), "to", float(lon[-1]))
    if var.shape != (12, 4320, 8640):
        raise SystemExit("Unexpected climate grid size")
    if not (lat[0] > lat[-1] and lon[0] < lon[-1] and abs(float(lon[0]) + 180.0) < 0.1):
        raise SystemExit("Expected rows north to south and columns from 180 W")


def to_i16(data, scale):
    scaled = np.rint(data * scale)
    scaled = np.where(np.isnan(scaled), NODATA, scaled)
    return np.clip(scaled, -32768, 32767).astype("<i2")


# ---- Climate ----
tmax = netCDF4.Dataset(f"{src}/tmax.nc")
tmin = netCDF4.Dataset(f"{src}/tmin.nc")
ppt = netCDF4.Dataset(f"{src}/ppt.nc")
check_grid(tmax, "tmax")
check_grid(tmin, "tmin")
check_grid(ppt, "ppt")

with open(f"{out}/climate_tmean.i16", "wb") as f_t, open(f"{out}/climate_ppt.i16", "wb") as f_p:
    for month in range(12):
        mean = (downsample(read_month(tmax, "tmax", month)) + downsample(read_month(tmin, "tmin", month))) / 2.0
        rain = downsample(read_month(ppt, "ppt", month))
        valid = int(np.count_nonzero(~np.isnan(mean)))
        print(
            f"month {month + 1}: valid pixels {valid}, "
            f"temperature {np.nanmin(mean):.1f} to {np.nanmax(mean):.1f} C, "
            f"rain {np.nanmin(rain):.0f} to {np.nanmax(rain):.0f} mm"
        )
        f_t.write(to_i16(mean, 10.0).tobytes())
        f_p.write(to_i16(rain, 1.0).tobytes())


def pixel(lon, lat):
    return ((lon + 180.0) * WIDTH / 360.0, (90.0 - lat) * HEIGHT / 180.0)


def record_dict(shape_record):
    return {k.lower(): v for k, v in shape_record.record.as_dict().items()}


def rings(shape):
    parts = list(shape.parts) + [len(shape.points)]
    for i in range(len(parts) - 1):
        yield shape.points[parts[i]:parts[i + 1]]


def signed_area(ring):
    total = 0.0
    for (x1, y1), (x2, y2) in zip(ring, ring[1:]):
        total += x1 * y2 - x2 * y1
    return total / 2.0


# ---- Lakes ----
# Ring direction is not reliable in this dataset, so each lake is filled with the even-odd rule:
# a pixel is lake if it lies inside an odd number of the lake's rings (islands are holes).
lake_image = Image.new("L", (WIDTH, HEIGHT), 0)
kinds = collections.Counter()
lake_count = 0
for sr in shapefile.Reader(f"{src}/ne_10m_lakes").iterShapeRecords():
    record = record_dict(sr)
    kind = str(record.get("featurecla", ""))
    kinds[kind] += 1
    if kind not in ("Lake", "Alkaline Lake"):
        continue
    pixel_rings = [[pixel(x, y) for x, y in ring] for ring in rings(sr.shape) if len(ring) >= 3]
    if not pixel_rings:
        continue
    xs = [p[0] for ring in pixel_rings for p in ring]
    ys = [p[1] for ring in pixel_rings for p in ring]
    x0, y0 = max(0, int(min(xs)) - 1), max(0, int(min(ys)) - 1)
    x1, y1 = min(WIDTH, int(max(xs)) + 2), min(HEIGHT, int(max(ys)) + 2)
    if x1 <= x0 or y1 <= y0:
        continue
    inside = Image.new("1", (x1 - x0, y1 - y0), 0)
    for ring in pixel_rings:
        layer = Image.new("1", inside.size, 0)
        ImageDraw.Draw(layer).polygon([(x - x0, y - y0) for x, y in ring], fill=1)
        inside = ImageChops.logical_xor(inside, layer)
    lake_image.paste(1, (x0, y0), inside)
    lake_count += 1
    if "caspian" in str(record.get("name", "")).lower():
        print("Caspian Sea found, pixels", sum(inside.getdata()) // 255 if inside.mode != "1" else sum(1 for v in inside.getdata() if v))
lakes = np.asarray(lake_image, dtype=np.uint8)
print("lake kinds", dict(kinds))
print("lakes drawn", lake_count, "lake pixels", int(lakes.sum()))
lakes.tofile(f"{out}/lakes.u8")

# ---- Rivers ----
river_image = Image.new("L", (WIDTH, HEIGHT), 0)
draw = ImageDraw.Draw(river_image)
ranks = collections.Counter()
river_kinds = collections.Counter()
lines = []
for sr in shapefile.Reader(f"{src}/ne_10m_rivers_lake_centerlines").iterShapeRecords():
    record = record_dict(sr)
    kind = str(record.get("featurecla", ""))
    river_kinds[kind] += 1
    if "canal" in kind.lower():
        continue
    try:
        rank = int(float(record.get("scalerank", 99)))
    except (TypeError, ValueError):
        rank = 99
    ranks[rank] += 1
    if rank > MINOR_RIVER_MAX_RANK:
        continue
    size = 2 if rank <= MAJOR_RIVER_MAX_RANK else 1
    for part in rings(sr.shape):
        if len(part) >= 2:
            lines.append((size, part))

# Draw minor rivers first so major ones win where they overlap.
for size, part in sorted(lines, key=lambda item: item[0]):
    draw.line([pixel(x, y) for x, y in part], fill=size, width=1)
rivers = np.asarray(river_image, dtype=np.uint8)
print("river kinds", dict(river_kinds))
print("river ranks", dict(sorted(ranks.items())))
print("river lines", len(lines), "minor pixels", int((rivers == 1).sum()), "major pixels", int((rivers == 2).sum()))
rivers.tofile(f"{out}/rivers.u8")

# River lines for the viewer: count, then per line a size byte, a point count and lon/lat pairs.
with open(river_lines_path, "wb") as f:
    f.write(b"SIM2RIVR")
    f.write(struct.pack("<i", len(lines)))
    points_written = 0
    for size, part in lines:
        f.write(struct.pack("<Bi", size, len(part)))
        f.write(np.asarray(part, dtype="<f4").tobytes())
        points_written += len(part)
print("wrote", river_lines_path, "with", points_written, "points")
