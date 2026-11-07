using UnityEngine;
using Verse;

namespace Lilly.CheatWork
{
    public sealed class CheatWorkMod : Mod
    {
        private readonly CheatWorkSettings settings;

        public static CheatWorkMod Instance { get; private set; }

        public CheatWorkMod(ModContentPack content) : base(content)
        {
            Instance = this;
            settings = GetSettings<CheatWorkSettings>();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.CheckboxLabeled("작업 지정을 즉시 완료", ref settings.Enabled);
            listing.Label("켜져 있으면 지정 순간에 작업 결과를 생성합니다. 림에게 작업을 예약하지 않습니다.");
            listing.End();
        }

        public override string SettingsCategory()
        {
            return "Cheat Work";
        }

        public static bool Enabled
        {
            get { return Instance == null || Instance.settings == null || Instance.settings.Enabled; }
        }
    }

    public sealed class CheatWorkSettings : ModSettings
    {
        public bool Enabled = true;

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Values.Look(ref Enabled, "enabled", true);
        }
    }
}
