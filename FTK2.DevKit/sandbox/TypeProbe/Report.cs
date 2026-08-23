using System;
using System.Text;

namespace TypeProbe
{
    /// <summary>
    /// Renders a <see cref="ProbeResult"/> as the markdown field-map table the grounding rule
    /// (spec §2) requires be committed to docs/research before code names a game member.
    /// A not-found result renders an explicit NOT FOUND marker: an empty table would read as
    /// "this type has no members", which is exactly the silent-failure class this tool exists
    /// to eliminate.
    /// </summary>
    public static class Report
    {
        public static string ToMarkdown(ProbeResult result)
        {
            StringBuilder sb = new StringBuilder();

            if (result == null || !result.Found)
            {
                string requested = result == null ? "(null)" : result.RequestedName;
                sb.AppendLine("## " + requested + " — **NOT FOUND**");
                sb.AppendLine();
                sb.AppendLine("The type was not present in the loaded assembly. Do not write code against it.");
                return sb.ToString();
            }

            sb.AppendLine("## " + result.FullName);
            sb.AppendLine();
            bool anySignature = false;
            foreach (MemberEntry m in result.Members)
                if (!string.IsNullOrEmpty(m.Signature)) { anySignature = true; break; }

            if (anySignature)
            {
                sb.AppendLine("| Kind | Type | Name | Signature |");
                sb.AppendLine("|---|---|---|---|");
                foreach (MemberEntry m in result.Members)
                    sb.AppendLine("| " + m.Kind + " | `" + m.TypeName + "` | `" + m.Name + "` | "
                        + (string.IsNullOrEmpty(m.Signature) ? "" : "`" + m.Signature + "`") + " |");
            }
            else
            {
                sb.AppendLine("| Kind | Type | Name |");
                sb.AppendLine("|---|---|---|");
                foreach (MemberEntry m in result.Members)
                    sb.AppendLine("| " + m.Kind + " | `" + m.TypeName + "` | `" + m.Name + "` |");
            }

            return sb.ToString();
        }
    }
}
