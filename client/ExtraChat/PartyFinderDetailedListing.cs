using Dalamud.Utility;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using Lumina.Text.ReadOnly;

namespace ExtraChat;

public class PartyFinderDetailedListing
{
    public ulong ListingId { get; set; }
    
    public AgentLookingForGroup.DutyCategory Category { get; set; }
    public ushort DutyId { get; set; }
    public ushort World { get; set; }
    public AgentLookingForGroup.Objective Objective { get; set; }
    public byte BeginnerFriendly { get; set; }
    public AgentLookingForGroup.CompletionStatus CompletionStatus { get; set; }
    public AgentLookingForGroup.DutyFinderSetting DutyFinderSettingFlags { get; set; }
    public AgentLookingForGroup.LootRule LootRule { get; set; }
    
    public uint TimeLeft { get; set; }
    
    public ushort AvgItemLv { get; set; }
    public ushort HomeWorld { get; set; }
    public ushort CurrentWorld { get; set; }
    public AgentLookingForGroup.Language LeaderClientLanguage { get; set; }
    public AgentLookingForGroup.Language LanguageFlags { get; set; }
    public byte TotalSlots { get; set; }
    public byte SlotsFilled { get; set; }
    
    public AgentLookingForGroup.JoinCondition JoinConditionFlags { get; set; }
    public bool IsAlliance { get; set; }
    public byte NumberOfParties { get; set; }

    public ulong[] SlotFlags { get; set; }
    public int[] Jobs { get; set; }

    public string Leader { get; set; }
    public string Comment { get; set; }

    public PartyFinderDetailedListing(AgentLookingForGroup.Detailed detailed)
    {
        ListingId = detailed.ListingId;
        
        Category = detailed.Category;
        DutyId = detailed.DutyId;
        World = detailed.World;
        Objective = detailed.Objective;
        BeginnerFriendly = detailed.BeginnerFriendly;
        CompletionStatus = detailed.CompletionStatus;
        DutyFinderSettingFlags = detailed.DutyFinderSettingFlags;
        LootRule = detailed.LootRule;

        TimeLeft = detailed.TimeLeft;

        AvgItemLv = detailed.AvgItemLv;
        HomeWorld = detailed.HomeWorld;
        CurrentWorld = detailed.CurrentWorld;
        LeaderClientLanguage = detailed.LeaderClientLanguage;
        LanguageFlags = detailed.LanguageFlags;
        TotalSlots = detailed.TotalSlots;
        SlotsFilled = detailed.SlotsFilled;

        JoinConditionFlags = detailed.JoinConditionFlags;
        IsAlliance = detailed.IsAlliance;
        NumberOfParties = detailed.NumberOfParties;

        SlotFlags = detailed.SlotFlags.ToArray();
        Jobs = new int[detailed.Jobs.Length];

        for (var i = 0; i < detailed.Jobs.Length; i++)
            Jobs[i] = detailed.Jobs[i];

        Leader = Convert.ToBase64String(detailed.Leader.BeforeNull());
        Comment = Convert.ToBase64String(detailed.Comment.BeforeNull());
    }
}
