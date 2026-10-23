using System;

namespace TerrarAI.Content.Companion;

public sealed record PlatformMaterialBudget(int MissingPlatforms, int Batches, int WoodNeeded, int WoodShortfall, int Output)
{
    public static PlatformMaterialBudget Calculate(int needed, int platforms, int wood, int input, int output)
    {
        if (needed < 0 || platforms < 0 || wood < 0 || input <= 0 || output <= 0)
            throw new ArgumentException("材料与配方数量无效。");
        int missing = Math.Max(0, needed - platforms);
        int batches = checked((missing + output - 1) / output);
        int cost = checked(batches * input);
        return new(missing, batches, cost, Math.Max(0, cost - wood), checked(batches * output));
    }
}

public enum SupplyStage { Gathering, Crafting, Building, Completed, Failed, Cancelled }
public enum HarvestStep { Pending, WaitingForApproval, Finished, Failed }
public interface IPlatformSupplySite : IPlatformSite
{
    int Wood { get; }
    HarvestStep Harvest(out string message);
    bool Craft(int batches, out string message);
    void CancelHarvest();
}

// No game/model dependencies: preparation advances on observed inventory, placement on the existing audit.
public sealed class PlatformSupplyJob
{
    public PlatformBuildJob Build { get; }
    public SupplyStage Stage { get; private set; } = SupplyStage.Gathering;
    public string Reason { get; private set; } = "";
    public int Crafted { get; private set; }
    public int WoodConsumed { get; private set; }
    public int Input { get; }
    public int Output { get; }
    public PlatformMaterialBudget Budget(IPlatformSupplySite site) =>
        PlatformMaterialBudget.Calculate(Build.Blueprint.Needed, site.Available, site.Wood, Input, Output);
    public PlatformSupplyJob(PlatformBlueprint blueprint, int input, int output)
    {
        if (input <= 0 || output <= 0) throw new ArgumentException("配方数量无效。");
        Build = new(blueprint); Input = input; Output = output;
    }
    public void Step(IPlatformSupplySite site)
    {
        if (Stage is SupplyStage.Completed or SupplyStage.Cancelled or SupplyStage.Failed) return;
        if (Stage == SupplyStage.Gathering)
        {
            var budget = Budget(site);
            if (budget.MissingPlatforms == 0) { site.CancelHarvest(); Stage = SupplyStage.Building; Reason = "成品平台已足够，开始施工。"; }
            else if (budget.WoodShortfall == 0) { site.CancelHarvest(); Stage = SupplyStage.Crafting; Reason = "普通木材已备齐，准备合成平台。"; }
            else
            {
                var result = site.Harvest(out string message); Reason = message;
                if (result == HarvestStep.Failed) Fail(site, message);
            }
            return;
        }
        if (Stage == SupplyStage.Crafting)
        {
            var budget = Budget(site);
            if (budget.WoodShortfall > 0) { Fail(site, "合成前材料被移走，请重新确认任务。"); return; }
            if (budget.Batches > 0)
            {
                int wood = site.Wood, platforms = site.Available;
                if (!site.Craft(budget.Batches, out string message)) { Fail(site, message); return; }
                WoodConsumed = wood - site.Wood; Crafted = site.Available - platforms;
                if (WoodConsumed != budget.WoodNeeded || Crafted != budget.Output)
                { Fail(site, "合成收据与实际背包变化不符，已停止。"); return; }
            }
            Stage = SupplyStage.Building; Reason = "备料完成，开始施工。"; return;
        }
        Build.Step(site);
        if (Build.State == PlatformJobState.Completed) { Stage = SupplyStage.Completed; Reason = Build.Reason; }
        else if (Build.State == PlatformJobState.Failed) Fail(site, Build.Reason);
    }
    public void Cancel(IPlatformSupplySite site, string reason)
    {
        if (Stage is SupplyStage.Completed or SupplyStage.Failed or SupplyStage.Cancelled) return;
        site.CancelHarvest(); Build.Cancel(reason); Stage = SupplyStage.Cancelled; Reason = reason;
    }
    public void Abort(IPlatformSupplySite site, string reason)
    { if (Stage is SupplyStage.Gathering or SupplyStage.Crafting or SupplyStage.Building) Fail(site, reason); }
    private void Fail(IPlatformSupplySite site, string reason)
    { site.CancelHarvest(); Build.Cancel(reason); Stage = SupplyStage.Failed; Reason = reason; }
}
