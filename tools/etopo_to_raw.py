"""Convert an ETOPO 2022 60 arc-second netCDF file to raw 16-bit little-endian
elevations, rows north to south, columns 180 W to 180 E.

Usage: python etopo_to_raw.py input.nc output.i16
"""
import sys

import netCDF4
import numpy as np

src, dst = sys.argv[1], sys.argv[2]
ds = netCDF4.Dataset(src)
ds.set_auto_mask(False)

var = None
for v in ds.variables.values():
    if v.ndim == 2:
        var = v
        break
if var is None:
    raise SystemExit("No 2D variable found")

lat_name, lon_name = var.dimensions
print("variable", var.name, "dims", var.dimensions, "shape", var.shape, "dtype", var.dtype)
if "lat" not in lat_name.lower() or "lon" not in lon_name.lower():
    raise SystemExit("Expected dimensions (lat, lon)")

height, width = var.shape
if (height, width) != (10800, 21600):
    raise SystemExit("Unexpected grid size")

lat = ds.variables[lat_name][:]
lon = ds.variables[lon_name][:]
print("lat", float(lat[0]), "to", float(lat[-1]), "lon", float(lon[0]), "to", float(lon[-1]))
if not (lon[0] < lon[-1] and abs(float(lon[0]) + 180.0) < 0.1):
    raise SystemExit("Expected longitude to run from -180 to 180")

north_first = lat[0] > lat[-1]
chunk = 600
lo, hi = 10**9, -10**9
with open(dst, "wb") as out:
    for i in range(0, height, chunk):
        if north_first:
            block = var[i:i + chunk, :]
        else:
            block = var[height - i - chunk:height - i, :][::-1]
        block = np.rint(np.asarray(block, dtype=np.float64))
        lo = min(lo, int(block.min()))
        hi = max(hi, int(block.max()))
        out.write(block.astype("<i2").tobytes())

print("wrote", dst, "min", lo, "max", hi)
