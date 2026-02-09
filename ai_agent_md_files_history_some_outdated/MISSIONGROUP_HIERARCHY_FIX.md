# MissionGroup Hierarchy Fix

## Issue
The `MissionGroupCopier.cs` was not creating the proper BeamNG level hierarchy structure. It was missing the required `items.level.json` files at intermediate levels of the directory structure.

## Required BeamNG Level Structure

According to BeamNG level file format, the MissionGroup hierarchy must have SimGroup entries at multiple levels:

```
levels/{levelname}/
??? main/
?   ??? items.level.json                          # Contains MissionGroup SimGroup
?   ??? MissionGroup/
?       ??? items.level.json                      # Contains Level_object SimGroup
?       ??? Level_object/
?           ??? items.level.json                  # Contains actual level objects
```

### Level 1: main/items.level.json
```json
{"name":"MissionGroup","class":"SimGroup","persistentId":"<guid>","enabled":"1"}
```

### Level 2: main/MissionGroup/items.level.json
```json
{"name":"Level_object","class":"SimGroup","persistentId":"<guid>","__parent":"MissionGroup"}
```

### Level 3: main/MissionGroup/Level_object/items.level.json
```json
{"class":"LevelInfo","persistentId":"<guid>","__parent":"Level_object",...}
{"class":"TerrainBlock","persistentId":"<guid>","__parent":"Level_object",...}
{"class":"TimeOfDay","persistentId":"<guid>","__parent":"Level_object",...}
...
```

## Changes Made

### 1. Added `CreateMissionGroupHierarchy()` Method

This new private method creates the proper hierarchy structure:

```csharp
private void CreateMissionGroupHierarchy()
{
    // 1. Create main/items.level.json with MissionGroup entry
    var mainItemsPath = Path.Join(_targetLevelNamePath, "main", "items.level.json");
    var missionGroupEntry = new Dictionary<string, object>
    {
        { "name", "MissionGroup" },
        { "class", "SimGroup" },
        { "persistentId", Guid.NewGuid().ToString() },
        { "enabled", "1" }
    };
    var missionGroupJson = JsonSerializer.Serialize(missionGroupEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions());
    File.WriteAllText(mainItemsPath, missionGroupJson + Environment.NewLine);
    
    // 2. Create main/MissionGroup/items.level.json with Level_object entry
    var missionGroupItemsPath = Path.Join(_targetLevelNamePath, "main", "MissionGroup", "items.level.json");
    var levelObjectEntry = new Dictionary<string, object>
    {
        { "name", "Level_object" },
        { "class", "SimGroup" },
        { "persistentId", Guid.NewGuid().ToString() },
        { "__parent", "MissionGroup" }
    };
    var levelObjectJson = JsonSerializer.Serialize(levelObjectEntry, BeamJsonOptions.GetJsonSerializerOneLineOptions());
    File.WriteAllText(missionGroupItemsPath, levelObjectJson + Environment.NewLine);
    
    // 3. main/MissionGroup/Level_object/items.level.json will be created by WriteMissionGroupItems()
}
```

### 2. Updated `CreateDirectoryStructure()` Method

Added call to `CreateMissionGroupHierarchy()` after creating directories:

```csharp
private void CreateDirectoryStructure()
{
    var directories = new[]
    {
        Path.Join(_targetLevelNamePath, "main", "MissionGroup", "Level_object"),
        Path.Join(_targetLevelNamePath, "art", "skies"),
        Path.Join(_targetLevelNamePath, "art", "terrains")
    };

    foreach (var dir in directories)
    {
        Directory.CreateDirectory(dir);
        PubSubChannel.SendMessage(PubSubMessageType.Info, $"Created directory: {dir}", true);
    }
    
    // Create hierarchy items.level.json files
    CreateMissionGroupHierarchy();
}
```

### 3. Added PersistentId Regeneration

Updated `WriteMissionGroupItems()` to generate new GUIDs for all copied objects to ensure uniqueness:

```csharp
// Generate new persistentId for copied objects
if (jsonDict.ContainsKey("persistentId"))
{
    jsonDict["persistentId"] = JsonSerializer.SerializeToElement(Guid.NewGuid().ToString());
}
```

This ensures that:
- Each copied object gets a unique identifier in the new level
- No GUID conflicts occur between source and target levels
- BeamNG can properly track and manage each object

## Benefits

1. **Proper BeamNG Level Structure**: Levels created by the wizard will now have the correct hierarchy that BeamNG expects
2. **Valid Scene Graph**: The parent-child relationships between SimGroups are correctly established
3. **Unique Identifiers**: All objects have unique persistentIds, preventing conflicts
4. **Engine Compatibility**: BeamNG.drive will properly recognize and load the created levels

## Testing

Build successful ?

To test the complete functionality:
1. Run the Create Level wizard
2. Initialize a new level from a source (e.g., driver_training)
3. Check that the following files are created:
   - `levels/{newlevel}/main/items.level.json` (contains MissionGroup)
   - `levels/{newlevel}/main/MissionGroup/items.level.json` (contains Level_object)
   - `levels/{newlevel}/main/MissionGroup/Level_object/items.level.json` (contains level objects)
4. Verify each file has proper JSON structure and unique GUIDs
5. Load the level in BeamNG.drive to verify it's recognized as a valid level

## Related Files

- `BeamNG_LevelCleanUp/LogicCopyAssets/MissionGroupCopier.cs` (modified)
- `BeamNG_LevelCleanUp/Objects/CreateLevelWizardState.cs` (uses MissionGroupCopier)
- `.github/copilot-instructions.md` (documents BeamNG file format)

## References

See the "Level Directory Structure" and "Level Files (items.level.json)" sections in `.github/copilot-instructions.md` for complete BeamNG file format documentation.
