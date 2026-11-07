# Scene upgrade fixtures

`TestSceneUpgrader` opens each scene of `Fixtures/` as a project upgrade does: the asset migration, then the load with
the types of today. It saves it again and compares the result with the file of the same name in `Expected/`.

The fixtures are scenes as a released version shipped them, taken from the release tags of this repository, so they are
MIT like the rest of it. Keep them as they are: a fixture is what an old project has on disk.

| Fixture | Release tag | Path at that tag |
|---|---|---|
| `4.0-NavigationTests-DynamicBarrierTest.sdscene` | `releases/4.0.1.1428` | `sources/engine/Stride.Navigation.Tests/Assets/Shared/DynamicBarrierTest.sdscene` |
| `4.0-TopDownRPG-DynamicScene.sdscene` | `releases/4.0.1.1428` | `samples/Templates/TopDownRPG/TopDownRPG/Assets/DynamicScene.sdscene` |
| `4.1-CSharpIntermediate-Completed-Audio.sdscene` | `releases/4.1.0.1948` | `samples/Tutorials/CSharpIntermediate/CSharpIntermediate/Assets/Scenes/08_Audio/Completed-Audio.sdscene` |
| `4.2-BepuSample-Constraint.sdscene` | `releases/4.2.1.2487` | `samples/Physics/BepuSample/Assets/Shared/Scenes/Constraint.sdscene` |
| `4.3-CSharpBeginner-MainScene.sdscene` | `releases/4.3.0.2507` | `samples/Tutorials/CSharpBeginner/CSharpBeginner/Assets/Scenes/MainScene.sdscene` |
| `4.4-TopDownRPG-DynamicScene.sdscene` | `releases/4.4.1` | `samples/Templates/TopDownRPG/TopDownRPG/Assets/DynamicScene.sdscene` |

Each sample scene was added or changed in the release it is named after, so that release saved it. The scene format
has been `{Stride: 3.1.0.1}` since before 4.0, and no sample of a release is older. The navigation test scene is: 4.0
still shipped it at `2.1.0.1`, as an older version saved it, and it is the only fixture that goes through a migration
(`CharacterComponent.Gravity` from a float to a vector). For the others, the upgrade is the load
with the types of today: new members, renamed or removed ones, and the order and format they are written in.

The test fails when:

- the upgrade logs an error, or a warning other than a script of the sample that can't load (its game assembly isn't
  here): an engine type that no longer loads fails the test;
- the upgrade changes nothing in a fixture, which then tests nothing: replace it with an older scene;
- the result differs from `Expected/`: the result is written to `stride-scene-upgrade/` in the temp folder.

When the change is expected, write the expected files again and review their diff:

```
set STRIDE_SCENE_UPGRADE_UPDATE=1
dotnet test sources\engine\Stride.Assets.Tests --filter FullyQualifiedName~TestSceneUpgrader
```
