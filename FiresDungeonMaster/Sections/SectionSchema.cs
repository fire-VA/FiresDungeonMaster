using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using UnityEngine;

namespace FiresDungeonMaster.Sections
{
    /// <summary>
    /// SectionSchema - reader and writer for .blueprint files (Infinity-Hammer/PlanBuild format)
    /// extended with FiresDungeonMaster custom headers.
    ///
    /// READER (ParseFile):
    ///   - Parses standard PlanBuild .blueprint format (headers + piece lines).
    ///   - Recognizes #fdm_* headers (additive metadata).
    ///   - Unknown # headers are safely skipped (stock parser compatible).
    ///   - Piece line format: name;id;x;y;z;qx;qy;qz;qw;extraInfo;sx;sy;sz;data;chance (15 cols, 0-14).
    ///   - Numeric culture: invariant (dots not commas); input commas pre-swapped to dots.
    ///
    /// WRITER (WriteFile):
    ///   - Emits .blueprint text with standard headers + #fdm_* custom headers + piece lines.
    ///   - Piece lines byte-symmetric to loader's column layout (15 fields).
    ///   - All numerics in invariant culture (dots).
    ///   - Round-trips losslessly: .blueprint -> SectionBlueprint -> .blueprint.
    ///   - Stock PlanBuild/InfinityHammer loaders skip unknown #fdm_* headers via continue.
    /// </summary>
    public static class SectionSchema
    {
        private enum Section { Pieces, TerrainHeight, TerrainPaint, SnapPoints }

        /// <summary>
        /// Parse a .blueprint file into a SectionBlueprint.
        /// Throws on unrecoverable errors (missing core data, parse failures).
        /// Unknown #fdm_* headers are silently skipped (stock parser compatible).
        /// </summary>
        public static SectionBlueprint ParseFile(string filePath)
        {
            if (!System.IO.File.Exists(filePath))
                throw new InvalidOperationException($"Blueprint file not found: {filePath}");

            string[] rows = System.IO.File.ReadAllLines(filePath);
            // Strip a UTF-8 BOM off the first line — PlanBuild writes one, and it breaks the line-1 header parse
            // (#name/#fdm_ StartsWith checks) so the section reads as un-tagged.
            if (rows.Length > 0 && rows[0] != null) rows[0] = rows[0].TrimStart('\uFEFF');
            string fileId = System.IO.Path.GetFileNameWithoutExtension(filePath);
            var bp = new SectionBlueprint
            {
                // The file is named <SectionId>.blueprint by SaveSection, so the filename IS the canonical id.
                // (Previously this was left blank and ParsePlanBuild re-minted a DIFFERENT id → Delete "not found".)
                SectionId = fileId,
                Name = fileId,   // overridden by the #name: header below if present
                SourcePath = filePath,
                // Any #fdm_* header means WE authored this (a DungeonMaster section); otherwise it's a plain blueprint.
                IsDungeonSection = System.Array.Exists(rows, r => r != null && r.StartsWith("#fdm_", StringComparison.Ordinal)),
            };

            return ParsePlanBuild(bp, rows);
        }

        /// <summary>
        /// Write a SectionBlueprint to a .blueprint file.
        /// Emits standard headers + #fdm_* custom headers + piece section.
        /// Numerics are in invariant culture; piece lines have 15 columns.
        /// </summary>
        public static void WriteFile(string filePath, SectionBlueprint section)
        {
            if (section == null)
                throw new ArgumentNullException(nameof(section));

            var lines = new List<string>();

            // Standard headers (order matches InfinityHammer conventions).
            if (!string.IsNullOrEmpty(section.Name))
                lines.Add($"#name:{section.Name}");
            if (!string.IsNullOrEmpty(section.Description))
                lines.Add($"#description:{section.Description}");
            if (!string.IsNullOrEmpty(section.Category))
                lines.Add($"#category:{section.Category}");
            if (!string.IsNullOrEmpty(section.CenterPiece))
                lines.Add($"#center:{section.CenterPiece}");

            // Coordinates and rotation (XZY order per Valheim convention).
            if (section.Coordinates != Vector3.zero)
                lines.Add($"#coordinates:{FormatVectorXZY(section.Coordinates)}");
            if (section.Rotation != Vector3.zero)
                lines.Add($"#rotation:{FormatVectorXZY(section.Rotation)}");

            // FiresDungeonMaster custom headers (additive, safely skipped by stock parsers).
            if (!string.IsNullOrEmpty(section.DungeonType))
                lines.Add($"#fdm_dungeontype:{section.DungeonType}");
            lines.Add($"#fdm_footprint:{section.Footprint.x},{section.Footprint.y}");
            lines.Add($"#fdm_stories:{section.Stories}");
            lines.Add($"#fdm_role:{section.Role}");

            // Edge tags (per-face).
            foreach (var face in new[] { "north", "south", "east", "west" })
            {
                if (section.EdgeTags.TryGetValue(face, out var tags) && tags.Count > 0)
                    lines.Add($"#fdm_edge_{face}:{string.Join(";", tags)}");
            }

            if (!string.IsNullOrEmpty(section.SnapType))
                lines.Add($"#fdm_snaptype:{section.SnapType}");

            if (section.EntranceFace >= 0)
                lines.Add($"#fdm_entrance:{section.EntranceFace}");

            // Multi-cell SET headers (a diced scaled box): every cell of the set carries these so they place together.
            if (!string.IsNullOrEmpty(section.SetId))
            {
                lines.Add($"#fdm_set:{section.SetId}");
                lines.Add($"#fdm_setpos:{section.SetPos.x},{section.SetPos.y}");
                lines.Add($"#fdm_setsize:{section.SetSize.x},{section.SetSize.y}");
            }

            // Piece section header.
            lines.Add("#pieces");

            // Piece lines (15 columns, byte-symmetric to loader).
            foreach (var piece in section.Objects)
            {
                var line = FormatPieceLine(piece);
                lines.Add(line);
            }

            // Snap points (after pieces) — emitted so the round-trip is LOSSLESS; the stock PlanBuild/IH loader
            // reads this same #snappoints section, so it stays interoperable.
            if (section.SnapPoints.Count > 0)
            {
                lines.Add("#snappoints");
                foreach (var snap in section.SnapPoints)
                    lines.Add($"{FormatInvariant(snap.x)};{FormatInvariant(snap.y)};{FormatInvariant(snap.z)}");
            }

            // Write to file (UTF-8, no BOM for cross-platform compatibility).
            System.IO.File.WriteAllLines(filePath, lines, Encoding.UTF8);
        }

        /// <summary>
        /// Parse a PlanBuild .blueprint file (state-machine parser matching VABlueprintLoader).
        /// </summary>
        private static SectionBlueprint ParsePlanBuild(SectionBlueprint bp, string[] rows)
        {
            Section section = Section.Pieces;

            foreach (var raw in rows)
            {
                if (raw == null) continue;
                var row = raw;
                if (row.Length == 0) continue;

                if (row.StartsWith("#", StringComparison.Ordinal))
                {
                    // Parse standard headers.
                    if (HeaderEquals(row, "#name:", out var nm))
                        bp.Name = nm;
                    else if (HeaderEquals(row, "#description:", out var desc))
                        bp.Description = desc;
                    else if (HeaderEquals(row, "#category:", out var cat))
                        bp.Category = cat;
                    else if (HeaderEquals(row, "#center:", out var c))
                        bp.CenterPiece = c;
                    else if (HeaderEquals(row, "#coordinates:", out var coord))
                        bp.Coordinates = ParseVectorXZY(coord);
                    else if (HeaderEquals(row, "#rotation:", out var rotS))
                        bp.Rotation = ParseVectorXZY(rotS);

                    // Parse custom #fdm_* headers.
                    else if (HeaderEquals(row, "#fdm_dungeontype:", out var dungType))
                        bp.DungeonType = dungType;
                    else if (HeaderEquals(row, "#fdm_footprint:", out var fpStr))
                    {
                        var parts = fpStr.Split(',', ';');
                        if (parts.Length >= 2 &&
                            int.TryParse(parts[0].Trim(), out var fx) &&
                            int.TryParse(parts[1].Trim(), out var fz))
                            bp.Footprint = new Vector2Int(fx, fz);
                    }
                    else if (HeaderEquals(row, "#fdm_stories:", out var storiesStr))
                    {
                        if (int.TryParse(storiesStr.Trim(), out var stories) && stories > 0)
                            bp.Stories = stories;
                    }
                    else if (HeaderEquals(row, "#fdm_role:", out var roleStr))
                    {
                        if (Enum.TryParse<SectionRole>(roleStr.Trim(), ignoreCase: true, out var role))
                            bp.Role = role;
                    }
                    else if (HeaderEquals(row, "#fdm_edge_north:", out var edgeN))
                        bp.EdgeTags["north"] = edgeN.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    else if (HeaderEquals(row, "#fdm_edge_south:", out var edgeS))
                        bp.EdgeTags["south"] = edgeS.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    else if (HeaderEquals(row, "#fdm_edge_east:", out var edgeE))
                        bp.EdgeTags["east"] = edgeE.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    else if (HeaderEquals(row, "#fdm_edge_west:", out var edgeW))
                        bp.EdgeTags["west"] = edgeW.Split(';').Select(s => s.Trim()).Where(s => !string.IsNullOrEmpty(s)).ToList();
                    else if (HeaderEquals(row, "#fdm_snaptype:", out var snapT))
                        bp.SnapType = snapT;
                    else if (HeaderEquals(row, "#fdm_entrance:", out var entS))
                    {
                        if (int.TryParse(entS.Trim(), out var ent)) bp.EntranceFace = ent;
                    }
                    else if (HeaderEquals(row, "#fdm_set:", out var setId))
                        bp.SetId = setId;
                    else if (HeaderEquals(row, "#fdm_setpos:", out var setPos))
                    {
                        var p = setPos.Split(',', ';');
                        if (p.Length >= 2 && int.TryParse(p[0].Trim(), out var px) && int.TryParse(p[1].Trim(), out var pz))
                            bp.SetPos = new Vector2Int(px, pz);
                    }
                    else if (HeaderEquals(row, "#fdm_setsize:", out var setSize))
                    {
                        var p = setSize.Split(',', ';');
                        if (p.Length >= 2 && int.TryParse(p[0].Trim(), out var sx) && int.TryParse(p[1].Trim(), out var sz))
                            bp.SetSize = new Vector2Int(sx, sz);
                    }

                    // Section mode switches.
                    else if (row.StartsWith("#height:", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainHeight;
                    else if (row.StartsWith("#paint", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainPaint;
                    else if (row.StartsWith("#snappoints", StringComparison.OrdinalIgnoreCase))
                        section = Section.SnapPoints;
                    else if (row.StartsWith("#terrain", StringComparison.OrdinalIgnoreCase))
                        section = Section.TerrainHeight;
                    else if (row.StartsWith("#pieces", StringComparison.OrdinalIgnoreCase))
                        section = Section.Pieces;
                    // Unknown # headers are ignored (continue to next line).
                    continue;
                }

                // Parse data rows based on current section.
                switch (section)
                {
                    case Section.Pieces:
                        var piece = ParsePlanBuildPiece(row);
                        if (piece != null)
                            bp.Objects.Add(piece);
                        break;
                    case Section.SnapPoints:
                        bp.SnapPoints.Add(ParsePlanBuildSnapPoint(row));
                        break;
                    case Section.TerrainHeight:
                    case Section.TerrainPaint:
                        // Terrain sections are ignored (optional for sections).
                        break;
                }
            }

            // Mint a stable section ID if not already set.
            if (string.IsNullOrEmpty(bp.SectionId))
                bp.MintSectionId();

            return bp;
        }

        /// <summary>
        /// Test if a header line matches a prefix (case-insensitive).
        /// Returns the payload (text after colon) if matched; null otherwise.
        /// </summary>
        private static bool HeaderEquals(string row, string prefix, out string payload)
        {
            if (row.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                int colon = row.IndexOf(':');
                payload = colon >= 0 && colon + 1 < row.Length
                    ? row.Substring(colon + 1).Trim()
                    : string.Empty;
                return true;
            }
            payload = null;
            return false;
        }

        /// <summary>
        /// Parse a piece line (15 semicolon-separated columns).
        /// Matches VABlueprintLoader.ParsePlanBuildPiece logic exactly.
        /// </summary>
        private static Piece ParsePlanBuildPiece(string row)
        {
            if (row.IndexOf(',') > -1)
                row = row.Replace(',', '.');
            string[] cols = row.Split(';');
            if (cols.Length < 9)
                return null;

            string name = cols[0];
            if (string.IsNullOrEmpty(name))
                return null;

            float x = InvariantFloat(cols, 2);
            float y = InvariantFloat(cols, 3);
            float z = InvariantFloat(cols, 4);
            float qx = InvariantFloat(cols, 5);
            float qy = InvariantFloat(cols, 6);
            float qz = InvariantFloat(cols, 7);
            float qw = InvariantFloat(cols, 8);
            string info = cols.Length > 9 ? cols[9] : "";
            float sx = InvariantFloat(cols, 10, 1f);
            float sy = InvariantFloat(cols, 11, 1f);
            float sz = InvariantFloat(cols, 12, 1f);
            string data = cols.Length > 13 ? cols[13] : "";
            float chance = InvariantFloat(cols, 14, 1f);

            return new Piece
            {
                Prefab = name,
                Pos = new Vector3(x, y, z),
                Rot = new Quaternion(qx, qy, qz, qw),
                Scale = new Vector3(sx, sy, sz),
                ExtraInfo = info,
                Data = data,
                Chance = chance,
            };
        }

        /// <summary>
        /// Parse a snap-point line (3 semicolon-separated floats: x;y;z).
        /// </summary>
        private static Vector3 ParsePlanBuildSnapPoint(string row)
        {
            if (row.IndexOf(',') > -1)
                row = row.Replace(',', '.');
            string[] cols = row.Split(';');
            return new Vector3(
                InvariantFloat(cols, 0),
                InvariantFloat(cols, 1),
                InvariantFloat(cols, 2));
        }

        /// <summary>
        /// Parse a float from a string array with optional default.
        /// Uses invariant culture; handles missing/malformed fields gracefully.
        /// </summary>
        private static float InvariantFloat(string[] row, int index, float def = 0f)
        {
            if (row == null || index >= row.Length)
                return def;
            return float.TryParse(row[index]?.Trim(), NumberStyles.Float,
                                  CultureInfo.InvariantCulture, out var v)
                ? v : def;
        }

        /// <summary>
        /// Format a Vector3 as "x,z,y" (XZY order per Valheim convention).
        /// </summary>
        private static string FormatVectorXZY(Vector3 v)
        {
            return $"{FormatInvariant(v.x)},{FormatInvariant(v.z)},{FormatInvariant(v.y)}";
        }

        /// <summary>
        /// Parse a "x,z,y" (XZY order, Valheim convention) header payload back into a Vector3 (x, y, z).
        /// Inverse of FormatVectorXZY; invariant culture, missing/short input -> Vector3.zero.
        /// </summary>
        private static Vector3 ParseVectorXZY(string s)
        {
            if (string.IsNullOrEmpty(s)) return Vector3.zero;
            string[] parts = s.Split(',');
            if (parts.Length < 3) return Vector3.zero;
            return new Vector3(
                InvariantFloat(parts, 0),   // x
                InvariantFloat(parts, 2),   // y (third column in XZY)
                InvariantFloat(parts, 1));  // z (second column in XZY)
        }

        /// <summary>
        /// Format a float in invariant culture (dots, no commas).
        /// </summary>
        private static string FormatInvariant(float value)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Format a piece as a 15-column semicolon-separated line.
        /// Column order: name;id;x;y;z;qx;qy;qz;qw;extraInfo;sx;sy;sz;data;chance
        /// (indices 0-14, matching VABlueprintLoader.ParsePlanBuildPiece).
        /// </summary>
        private static string FormatPieceLine(Piece piece)
        {
            var sb = new StringBuilder();
            sb.Append(piece.Prefab ?? "");
            sb.Append(";");
            sb.Append(""); // id (col 1) - always empty for round-trip losslessness.
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Pos.x));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Pos.y));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Pos.z));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Rot.x));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Rot.y));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Rot.z));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Rot.w));
            sb.Append(";");
            sb.Append(piece.ExtraInfo ?? ""); // col 9
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Scale.x));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Scale.y));
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Scale.z));
            sb.Append(";");
            sb.Append(piece.Data ?? ""); // col 13 (base64 blob)
            sb.Append(";");
            sb.Append(FormatInvariant(piece.Chance));
            return sb.ToString();
        }
    }
}
