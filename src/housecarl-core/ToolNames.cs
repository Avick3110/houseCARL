namespace HousecarlCore;

/// <summary>
/// Every tool name the shipped surface uses, as compile-time constants.
///
/// <para>One constant per DECLARED <c>[McpServerTool].Name</c> -- declared, not registered: a tool
/// the SDK does not scan still has its name spelled here. Retired spellings have no constant.</para>
///
/// <para><c>const</c>, not <c>static readonly</c>: these are spliced into attribute arguments, which
/// must be constant expressions.</para>
///
/// <para>Produced once by <c>scripts/tool-names/</c> -- the one-shot migration record of how this file
/// and the rewritten call sites were derived. Those scripts are not a maintenance path and are not
/// re-run. A new tool's constant is added BY HAND, one line; the completeness test holds this set
/// against the declared tools and fails until it is.
/// Rationale: <c>docs/decisions/0004-tool-names-are-compile-time-constants.md</c>.</para>
/// </summary>
public static class ToolNames
{
    public const string Apply = "housecarl_apply";
    public const string AssetStatus = "housecarl_asset_status";
    public const string BsaExtract = "housecarl_bsa_extract";
    public const string BsaList = "housecarl_bsa_list";
    public const string BsaRepack = "housecarl_bsa_repack";
    public const string BulkPlaceAsset = "housecarl_bulk_place_asset";
    public const string Check = "housecarl_check";
    public const string CompactPlugin = "housecarl_compact_plugin";
    public const string CompileScript = "housecarl_compile_script";
    public const string Copy = "housecarl_copy";
    public const string CopyNpcAppearance = "housecarl_copy_npc_appearance";
    public const string Create = "housecarl_create";
    public const string CreatePlugin = "housecarl_create_plugin";
    public const string DecompileScript = "housecarl_decompile_script";
    public const string Forward = "housecarl_forward";
    public const string LoadOrderStatus = "housecarl_load_order_status";
    public const string MergePlugins = "housecarl_merge_plugins";
    public const string NativePairingAudit = "housecarl_native_pairing_audit";
    public const string NexusCheckUpdates = "housecarl_nexus_check_updates";
    public const string NexusGraphql = "housecarl_nexus_graphql";
    public const string NexusIdentify = "housecarl_nexus_identify";
    public const string NexusMod = "housecarl_nexus_mod";
    public const string NexusSearch = "housecarl_nexus_search";
    public const string NifInspect = "housecarl_nif_inspect";
    public const string NifSet = "housecarl_nif_set";
    public const string PlaceAsset = "housecarl_place_asset";
    public const string Records = "housecarl_records";
    public const string Remove = "housecarl_remove";
    public const string SetMo2Instance = "housecarl_set_mo2_instance";
    public const string SetToolPath = "housecarl_set_tool_path";
    public const string SkseConfigAudit = "housecarl_skse_config_audit";
    public const string SkseInventory = "housecarl_skse_inventory";
    public const string SkypatcherLayer = "housecarl_skypatcher_layer";
    public const string UpdateStatus = "housecarl_update_status";
    public const string WriteSeq = "housecarl_write_seq";
}
