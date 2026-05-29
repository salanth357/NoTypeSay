using System;
using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace NoTypeSay;

// ReSharper disable once UnusedType.Global
public sealed unsafe partial class Plugin : IDalamudPlugin
{
    // The lower nibble of the upper byte of the uint value passed by the button is used by AgentHUD.ReceiveEvent
    // to determine what type of action the todolist button is. Currently, they only use 0-3:
    // 0: use object on target (I think?)
    // 1: crafting recipe
    // 2: emotes
    // 3: tutorial buttons
    // I'm claiming 9 here as something that's probably high enough to not be a problem any time soon
    private const int SayActionType = 0x09000000;
    private const int ActionValueMask = 0x00FFFFFF;
    private const int ActionTypeMask = unchecked((int)0xFF000000);

    // This is the IconId used for the button- we load it lazily here so that I don't have to update it when they move
    // the icons around again
    private readonly Lazy<uint> converseIconId = new(() => GetEmoteFromSlashCommand("/converse").Icon);

    private readonly List<Entry> questEntries = [];

    public Plugin()
    {
        AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, "_ToDoList", PreReceiveEvent);
        AddonLifecycle.RegisterListener(AddonEvent.PreRequestedUpdate, "_ToDoList", PreReqUpdate);

        if (ClientState.IsLoggedIn)
        {
            // if we're loading _while_ logged in, they installed from in-game, so we can just draw
            // otherwise, our hooks will run once login completes
            Framework.Run(ForceRedraw);
        }
    }

    private int ConverseIconId => (int)converseIconId.Value;

    [PluginService]
    internal static IDataManager DataManager { get; private set; } = null!;

    [PluginService]
    internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    [PluginService]
    internal static IPluginLog PluginLog { get; private set; } = null!;

    [PluginService]
    internal static IClientState ClientState { get; private set; } = null!;

    [PluginService]
    internal static IFramework Framework { get; private set; } = null!;

    private static StringArrayData* StringArray => AtkStage.Instance()->GetStringArrayData(StringArrayType.ToDoList);

    public void Dispose()
    {
        AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, "_ToDoList", PreReceiveEvent);
        AddonLifecycle.UnregisterListener(AddonEvent.PreRequestedUpdate, "_ToDoList", PreReqUpdate);

        // We should avoid modifying these from arbitrary threads, so clean ourselves up on the main thread
        Framework.Run(() =>
        {
            var numberArray = ToDoListNumberArray.Instance();
            var sad = StringArray;

            var offset = 0;
            foreach (var e in questEntries)
            {
                e.ResetTodoEntry(numberArray, sad, ref offset);
                e.Dispose();
            }

            questEntries.Clear();
            ForceRedraw();
        });
    }

    private static int MakeActionValue(int value)
    {
        return SayActionType | (value & ActionValueMask);
    }

    private static int GetActionValue(int value)
    {
        return value & ActionValueMask;
    }

    private static bool IsSayCommand(int value)
    {
        return (value & ActionTypeMask) == SayActionType;
    }

    private static Emote GetEmoteFromSlashCommand(string command)
    {
        var id = DataManager.Excel.GetSheet<TextCommand>().First(r => r.Command == command).RowId;
        return DataManager.Excel.GetSheet<Emote>().First(r => r.TextCommand.RowId == id);
    }

    private static void ForceRedraw()
    {
        StringArray->UpdateState = 1;
    }

    private void PreReqUpdate(AddonEvent type, AddonArgs addonArgs)
    {
        var numArray = ToDoListNumberArray.Instance();
        var stringArray = ToDoListStringArray.Instance();

        // UI disabled, just leave- we'll get another hook when the list comes back
        // so we'll update then.
        if (!numArray->QuestListEnabled) return;

        var offset = 0;
        foreach (var e in questEntries) e.Dispose();
        questEntries.Clear();
        var entryCount = numArray->QuestCount;
        for (var i = 0; i < entryCount; i++)
        {
            var e = new Entry
            {
                Index = i,
                TitleText = Utf8String.FromSequence(stringArray->QuestTexts[i]),
                DetailText = Utf8String.FromSequence(stringArray->QuestTexts[entryCount + i]),
                ButtonCount = numArray->ButtonCountForQuest[i],
            };

            // Populate the existing data into our record
            for (var j = 0; j < e.ButtonCount; j++)
            {
                e.ItemIds.Add(numArray->QuestButtonActionId[offset]);
                e.IconIds.Add(numArray->QuestButtonIconID[offset]);
                e.Strings.Add(Utf8String.FromSequence(stringArray->QuestStatusMessages[offset]));
                offset++;
            }

            // If we have a /say message, modify the button
            var msg = QuestMessage(e.TitleText->ToString(), e.DetailText->ToString());
            if (msg != "")
            {
                e.Message = $"/say {msg}";
                e.Modified = true;

                var newId = MakeActionValue(e.Index);
                var lastItemId = e.ItemIds.LastOrDefault();
                // check to see if our button is still present
                // if it is, we don't want to add another button
                // if the UI is full of buttons already somehow, don't add one either
                if (e.ButtonCount < 4 && !IsSayCommand(lastItemId))
                {
                    e.ItemIds.Add(newId);
                    e.IconIds.Add(ConverseIconId);
                    e.Strings.Add(Utf8String.FromString(msg));
                    e.ButtonCount++;
                }
                else if (IsSayCommand(lastItemId))
                {
                    // In this case, our button is still loaded from before somehow, so just
                    // store the string for it on our entry
                    e.ItemIds[^1] = newId;
                    e.Strings[^1] = Utf8String.FromString(msg);
                    e.IconIds[^1] = ConverseIconId;
                }
            }

            questEntries.Add(e);
        }

        if (questEntries.Count == 0) return;
        var sad = StringArray;
        offset = 0;
        foreach (var e in questEntries) e.WriteTodoEntry(numArray, sad, ref offset);
    }


    private void PreReceiveEvent(AddonEvent type, AddonArgs baseArgs)
    {
        try
        {
            var args = (AddonReceiveEventArgs)baseArgs;
            var eventType = args.AtkEventType;
            var atkEvent = (AtkEvent*)args.AtkEvent;
            var atkEventData = (AtkEventData*)args.AtkEventData;

            if (eventType != AddonEventType.MouseUp || args.EventParam != 1 || atkEventData->MouseData.ButtonId == 1)
                return;

            // Check the node pointer on the atkEvent for the action value. For some reason, they smuggle
            // the button action through here...
            if (atkEvent->Node == null)
            {
                PluginLog.Debug("no node param");
                return;
            }

            var buttonAction = *(int*)atkEvent->Node;
            if (!IsSayCommand(buttonAction))
            {
                PluginLog.Debug("not one of our buttons");
                return;
            }

            // Mask away the type flag- we store our index here
            var buttonIdx = GetActionValue(buttonAction);

            if (buttonIdx >= questEntries.Count)
            {
                PluginLog.Debug($"overshot the entries somehow- {buttonIdx} >= {questEntries.Count}");
                return;
            }

            var entry = questEntries[buttonIdx];
            if (entry is not { Modified: true })
            {
                PluginLog.Debug("unmodified entry");
                return;
            }

            // This is our event, so play the sound and send the message
            UIGlobals.PlaySoundEffect(8);
            SendChat(entry.Message);

            // We handled this, so prevent it from being sent to the native handler
            args.PreventOriginal();
        }
        catch (Exception ex)
        {
            // If something went wrong, log it and prevent the original- we don't want to send this
            // button since it might still be ours and we don't want the game to see our action type
            PluginLog.Error($"Caught exception in handleEvent: {ex}");
            baseArgs.PreventOriginal();
        }
    }

    private static void SendChat(string message)
    {
        using var str = new Utf8String(message);
        str.SanitizeString(AllowedEntities.Numbers | AllowedEntities.UppercaseLetters |
                           AllowedEntities.LowercaseLetters | AllowedEntities.OtherCharacters |
                           AllowedEntities.SpecialCharacters);
        // This is the max length the chatbox would allow someone to type in
        // so make sure we don't send anything longer than this
        if (str.StringLength > 500) return;
        UIModule.Instance()->ProcessChatBoxEntry(&str);
    }
}
