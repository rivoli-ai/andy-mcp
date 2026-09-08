"""Fail packaging when either shipped library loses required NuGet content."""
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
from zipfile import ZipFile

folder = Path(sys.argv[1] if len(sys.argv) > 1 else "artifacts")
packages = list(folder.glob("*.nupkg"))
assert len(packages) == 2, f"Expected two library packages, got {packages}"
seen = set()
for path in packages:
    with ZipFile(path) as archive:
        names = set(archive.namelist())
        manifest = ET.fromstring(archive.read(next(n for n in names if n.endswith(".nuspec"))))
        ns = {"n": manifest.tag.split("}")[0][1:]}
        metadata = manifest.find("n:metadata", ns)
        name = metadata.findtext("n:id", namespaces=ns)
        seen.add(name)
        for required in ["README.md", "andy_mcp_icon.png", f"lib/net10.0/{name}.dll", f"lib/net10.0/{name}.xml"]:
            assert required in names, f"{path}: missing {required}"
        assert all(n.startswith("lib/net10.0/") for n in names if n.startswith("lib/")), names
        assert metadata.find("n:repository", ns).get("url") == "https://github.com/rivoli-ai/andy-mcp"
        assert metadata.findtext("n:license", namespaces=ns) == "Apache-2.0"
        groups = metadata.findall("n:dependencies/n:group", ns)
        assert groups and all(g.get("targetFramework") == "net10.0" for g in groups)
        if name.endswith("AspNetCore"):
            assert any(d.get("id") == "Andy.MCP" for g in groups for d in g)
    with ZipFile(path.with_suffix(".snupkg")) as symbols:
        assert f"lib/net10.0/{name}.pdb" in symbols.namelist()
assert seen == {"Andy.MCP", "Andy.MCP.AspNetCore"}, seen
print("Both packages contain the expected .NET 10 assemblies, XML docs, symbols and metadata.")
