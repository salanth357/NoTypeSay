using System.Linq;
using System.Text.RegularExpressions;
using Dalamud.Game;
using Lumina.Excel;
using Lumina.Excel.Sheets;
using Lumina.Extensions;

namespace NoTypeSay;

// ReSharper disable once UnusedType.Global
public partial class Plugin
{
    // While we _could_ just keep a list of all /say quests, unless they
    // completely change the way they implement them, there's always a QuestDialogue
    // that contains _just_ the expected message, so we'll just look that up.
    // This approach also means we should support quests that have multiple /say steps
    private static string QuestMessage(string title, string detail)
    {
        var q = DataManager.GetExcelSheet<Quest>().FirstOrDefault(x => x.Name.ToString() == title);
        if (q.RowId == 0) return "";
        return TextSheetForQuest(q).FirstOrNull(qd => MessageMatch(detail, qd))?.Value.ToString() ?? "";
    }

    private static bool MessageMatch(string todoText, QuestDialogue qd)
    {
        if (!KeyRegex().IsMatch(qd.Key.ToString())) return false;
        if (qd.Value.IsEmpty) return false;
        var re = ClientState.ClientLanguage switch
        {
            ClientLanguage.English => EnFrRegex(),
            ClientLanguage.French => EnFrRegex(),
            ClientLanguage.Japanese => JpRegex(),
            ClientLanguage.German => DeRegex(),
            _ => EnFrRegex(), // This shouldn't really happen, but in the worst case, just try this
        };
        var message = qd.Value.ToString();
        return re.Matches(todoText).Any(m => m.Captures[0].Value.Contains(message));
    }

    [GeneratedRegex("“([^”]+)”")]
    private static partial Regex EnFrRegex();

    [GeneratedRegex("「([^」])+」|『([^』]+)』")]
    private static partial Regex JpRegex();

    [GeneratedRegex("„([^“]+)“")]
    private static partial Regex DeRegex();

    [GeneratedRegex("_(SAY|SAYTODO|SYSTEM)_")]
    private static partial Regex KeyRegex();

    private static ExcelSheet<QuestDialogue> TextSheetForQuest(Quest q)
    {
        var qid = q.Id.ToString();
        var dir = qid.Substring(qid.Length - 5, 3);
        return DataManager.GetExcelSheet<QuestDialogue>(name: $"quest/{dir}/{qid}");
    }
}
