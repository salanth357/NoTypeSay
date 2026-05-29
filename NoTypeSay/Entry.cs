using System;
using System.Collections.Generic;
using System.Text;
using FFXIVClientStructs.FFXIV.Client.System.String;
using FFXIVClientStructs.FFXIV.Client.UI.Arrays;
using FFXIVClientStructs.FFXIV.Component.GUI;
using FFXIVClientStructs.Interop;

namespace NoTypeSay;

internal sealed unsafe class Entry : IDisposable
{
    // These should never have more than 4 items
    internal readonly List<int> IconIds = [];
    internal readonly List<int> ItemIds = [];
    internal readonly List<Pointer<Utf8String>> Strings = [];

    internal int ButtonCount;
    internal Utf8String* DetailText; // used to id the quest step
    internal int Index;
    internal string Message = "";
    internal bool Modified;

    internal Utf8String* TitleText; // used to id the quest

    public void Dispose()
    {
        if (TitleText != null) TitleText->Dtor(true);
        if (DetailText != null) DetailText->Dtor(true);
        foreach (var s in Strings)
            if (s.Value != null)
                s.Value->Dtor(true);
    }

    // ReSharper disable once UnusedMember.Local
    public new string ToString()
    {
        var sb = new StringBuilder();
        sb.Append($"{Index}: [{(Modified ? 'x' : ' ')}] {ButtonCount} {TitleText->ToString()}");
        return sb.ToString();
    }

    internal void ResetTodoEntry(ToDoListNumberArray* nad, StringArrayData* sad, ref int offset)
    {
        // If we modified it AND we have buttons to remove,
        // decrement the button count (we're always the last one)
        if (Modified && ButtonCount > 0)
        {
            ButtonCount--;
            Modified = false;
        }

        WriteTodoEntry(nad, sad, ref offset);
    }

    internal void WriteTodoEntry(ToDoListNumberArray* nad, StringArrayData* sad, ref int offset)
    {
        nad->ButtonCountForQuest[Index] = ButtonCount;
        for (var i = 0; i < ButtonCount; i++)
        {
            // Don't propagate the update here since we'll be making a lot of changes
            // Also, we're either already _about_ to process an update or we'll request
            // one when we're done
            nad->QuestButtonActionId[offset] = ItemIds[i];
            nad->QuestButtonIconID[offset] = IconIds[i];
            sad->SetValueUtf8(offset + 119, Strings[i], suppressUpdates: true);
            offset++;
        }
    }
}
