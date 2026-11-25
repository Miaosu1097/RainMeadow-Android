"""Generate the Android local-address comparison change for Rain Meadow."""

import argparse
import shutil
from pathlib import Path

ROOT = Path(__file__).resolve().parents[3]
RELATIVE = Path("Online/Matchmaking/UdpPeer.cs")
ANCHOR = b"                var adapters = NetworkInterface.GetAllNetworkInterfaces();"
ANDROID = b"""                if (Application.platform == RuntimePlatform.Android)
                {
                    interface_addresses = AndroidLanAddresses.Read(message => RainMeadow.Debug(message));
                    return interface_addresses;
                }

"""


def workspace_path(value):
    path = Path(value).resolve()
    if not path.is_relative_to(ROOT):
        raise argparse.ArgumentTypeError(f"Path must be inside the workspace: {value}")
    return path


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("source", type=workspace_path, help="Unmodified Rain Meadow source")
    parser.add_argument("output", type=workspace_path, help="New directory for the generated source overlay")
    args = parser.parse_args()
    if args.output.exists() or args.output.is_relative_to(args.source):
        parser.error("Choose a new output directory outside the source tree")

    source_file = args.source / RELATIVE
    if source_file.resolve() != source_file or not source_file.is_file():
        parser.error(f"Missing or linked source file: {source_file}")
    original = source_file.read_bytes()
    if original.count(ANCHOR) != 1 or b"AndroidLanAddresses.Read(" in original:
        parser.error("The expected interface enumeration anchor is missing or ambiguous")

    line_end = b"\r\n" if original.count(b"\r\n") == original.count(b"\n") else b"\n"
    patched = original.replace(ANCHOR, ANDROID.replace(b"\n", line_end) + ANCHOR)
    target = args.output / RELATIVE
    target.parent.mkdir(parents=True)
    target.write_bytes(patched)
    shutil.copyfile(Path(__file__).with_name("AndroidLanAddresses.cs"), target.with_name("AndroidLanAddresses.cs"))
    print(f"Generated {target.relative_to(ROOT)} and AndroidLanAddresses.cs")


if __name__ == "__main__":
    main()
