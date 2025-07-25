﻿using System;
using System.Collections.Generic;
using Components;
using DefaultEcs;
using DefaultEcs.Threading;
using Godot;
using Systems;

/// <summary>
///   Handles displaying just microbe visuals (as alternative to the full <see cref="MicrobeWorldSimulation"/>)
/// </summary>
public sealed class MicrobeVisualOnlySimulation : WorldSimulation
{
    private readonly IMicrobeSpawnEnvironment dummyEnvironment = new DummyMicrobeSpawnEnvironment();

    private readonly List<Hex> hexWorkData1 = new();
    private readonly List<Hex> hexWorkData2 = new();

    // Base systems
    private AnimationControlSystem animationControlSystem = null!;
    private AttachedEntityPositionSystem attachedEntityPositionSystem = null!;
    private ColourAnimationSystem colourAnimationSystem = null!;
    private EntityMaterialFetchSystem entityMaterialFetchSystem = null!;
    private FadeOutActionSystem fadeOutActionSystem = null!;
    private PathBasedSceneLoader pathBasedSceneLoader = null!;
    private PredefinedVisualLoaderSystem predefinedVisualLoaderSystem = null!;

    private SpatialAttachSystem spatialAttachSystem = null!;
    private SpatialPositionSystem spatialPositionSystem = null!;

    // Microbe systems
    private CellBurstEffectSystem cellBurstEffectSystem = null!;

    // private ColonyBindingSystem colonyBindingSystem = null!;
    private MicrobeFlashingSystem microbeFlashingSystem = null!;
    private MicrobeRenderPrioritySystem microbeRenderPrioritySystem = null!;
    private MicrobeShaderSystem microbeShaderSystem = null!;
    private MicrobeVisualsSystem microbeVisualsSystem = null!;
    private TintColourApplyingSystem tintColourApplyingSystem = null!;

#pragma warning disable CA2213
    private Node visualsParent = null!;
#pragma warning restore CA2213

    /// <summary>
    ///   Initialized this visual simulation for use
    /// </summary>
    /// <param name="visualDisplayRoot">Root node to place all visuals under</param>
    public void Init(Node visualDisplayRoot)
    {
        disableComponentChecking = true;

        ResolveNodeReferences();

        visualsParent = visualDisplayRoot;

        // This is not used for intensive use, and even is used in the background of normal gameplay so this should use
        // just a single thread
        var runner = new DefaultParallelRunner(1);

        animationControlSystem = new AnimationControlSystem(EntitySystem);
        attachedEntityPositionSystem = new AttachedEntityPositionSystem(this, EntitySystem, runner);
        colourAnimationSystem = new ColourAnimationSystem(EntitySystem, runner);

        entityMaterialFetchSystem = new EntityMaterialFetchSystem(EntitySystem);
        fadeOutActionSystem = new FadeOutActionSystem(this, null, EntitySystem, runner);
        pathBasedSceneLoader = new PathBasedSceneLoader(EntitySystem, runner);

        predefinedVisualLoaderSystem = new PredefinedVisualLoaderSystem(EntitySystem);

        spatialAttachSystem = new SpatialAttachSystem(visualsParent, EntitySystem);
        spatialPositionSystem = new SpatialPositionSystem(EntitySystem);
        cellBurstEffectSystem = new CellBurstEffectSystem(EntitySystem);

        // For previewing multicellular some colony operations will be needed
        // colonyBindingSystem = new ColonyBindingSystem(this, EntitySystem, parallelRunner);

        microbeFlashingSystem = new MicrobeFlashingSystem(EntitySystem, runner);
        microbeRenderPrioritySystem = new MicrobeRenderPrioritySystem(EntitySystem);
        microbeShaderSystem = new MicrobeShaderSystem(EntitySystem);

        microbeVisualsSystem = new MicrobeVisualsSystem(EntitySystem);

        // organelleComponentFetchSystem = new OrganelleComponentFetchSystem(EntitySystem, runner);

        // TODO: is there a need for the movement system / OrganelleTickSystem to control animations on organelles
        // if those are used then also OrganelleComponentFetchSystem would be needed
        // organelleTickSystem = new OrganelleTickSystem(EntitySystem, runner);

        tintColourApplyingSystem = new TintColourApplyingSystem(EntitySystem);

        OnInitialized();
    }

    /// <summary>
    ///   Creates a simple visualization microbe in this world at origin that can then be manipulated with the microbe
    ///   visualization methods below
    /// </summary>
    /// <returns>The created entity</returns>
    public Entity CreateVisualisationMicrobe(Species species)
    {
        // TODO: should we have a separate spawn method to just spawn the visual aspects of a microbe?
        // The downside would be duplicated code, but it could skip the component types that don't impact the visuals

        // We pass AI controlled true here to avoid creating player specific data but as we don't have the AI system
        // it is fine to create the AI properties as it won't actually do anything
        SpawnHelpers.SpawnMicrobe(this, dummyEnvironment, species, Vector3.Zero, true);

        ProcessDelaySpawnedEntitiesImmediately();

        // Grab the created entity
        var foundEntity = GetLastMicrobeEntity();

        if (foundEntity == default)
            throw new Exception("Could not find microbe entity that should have been created");

        return foundEntity;
    }

    /// <summary>
    ///   Creates a simple visualization colony in this world at origin.
    /// </summary>
    /// <returns>The colony's root cell entity</returns>
    public Entity CreateVisualisationColony(MulticellularSpecies species)
    {
        // We pass AI controlled true here to avoid creating player specific data but as we don't have the AI system
        // it is fine to create the AI properties as it won't actually do anything
        SpawnHelpers.SpawnMicrobe(this, dummyEnvironment, species, Vector3.Zero, true);

        ProcessDelaySpawnedEntitiesImmediately();

        // Grab the created entity
        var foundEntity = GetLastMicrobeEntity();

        if (foundEntity == default)
            throw new Exception("Could not find microbe entity that should have been created");

        var recorder = StartRecordingEntityCommands();

        var dummySpawnSystem = new DummySpawnSystem();

        int count = species.Cells.Count;
        for (int i = 1; i < count; ++i)
        {
            var cell = species.Cells[i];

            DelayedColonyOperationSystem.CreateDelayAttachedMicrobe(ref foundEntity.Get<WorldPosition>(),
                in foundEntity, i, cell, species, this, dummyEnvironment, recorder, dummySpawnSystem, false);
        }

        recorder.Execute();
        FinishRecordingEntityCommands(recorder);

        return foundEntity;
    }

    public Entity GetLastMicrobeEntity()
    {
        Entity foundEntity = default;

        foreach (var entity in EntitySystem)
        {
            if (!entity.Has<CellProperties>())
                continue;

            // In case there are already multiple microbes, grab the last one
            foundEntity = entity;
        }

        return foundEntity;
    }

    public void ApplyNewVisualisationMicrobeSpecies(Entity microbe, MicrobeSpecies species)
    {
        if (!microbe.Has<CellProperties>())
        {
            GD.PrintErr("Can't apply new species to visualization entity as it is missing a component");
            return;
        }

        var dummyEffects = new MicrobeEnvironmentalEffects
        {
            HealthMultiplier = 1,
            OsmoregulationMultiplier = 1,
            ProcessSpeedModifier = 1,
        };

        // Do a full update apply with the general code method
        ref var cellProperties = ref microbe.Get<CellProperties>();
        cellProperties.ReApplyCellTypeProperties(ref dummyEffects, microbe, species,
            species, this, hexWorkData1, hexWorkData2);

        // TODO: update species member component if species changed?
    }

    /// <summary>
    ///   Applies just a colour value as the species colour to a microbe
    /// </summary>
    /// <param name="microbe">Microbe entity</param>
    /// <param name="colour">Colour to apply to it (overrides any previously applied species colour)</param>
    public void ApplyMicrobeColour(Entity microbe, Color colour)
    {
        if (!microbe.Has<CellProperties>())
        {
            GD.PrintErr("Can't apply new rigidity to visualization entity as it is missing a component");
            return;
        }

        ref var cellProperties = ref microbe.Get<CellProperties>();

        // Reset the initial used colour
        cellProperties.Colour = colour;

        // Reset the colour used when updating (should be fine to cancel the animation here)
        ref var colourComponent = ref microbe.Get<ColourAnimation>();
        colourComponent.DefaultColour = Membrane.MembraneTintFromSpeciesColour(colour);
        colourComponent.ResetColour();

        // We have to update all organelle visuals to get them to apply the new colour
        ref var organelleContainer = ref microbe.Get<OrganelleContainer>();
        organelleContainer.OrganelleVisualsCreated = false;
    }

    public void ApplyMicrobeRigidity(Entity microbe, float membraneRigidity)
    {
        if (!microbe.Has<CellProperties>())
        {
            GD.PrintErr("Can't apply new rigidity to visualization entity as it is missing a component");
            return;
        }

        ref var cellProperties = ref microbe.Get<CellProperties>();
        cellProperties.MembraneRigidity = membraneRigidity;

        ref var organelleContainer = ref microbe.Get<OrganelleContainer>();

        // Needed to re-apply membrane data
        organelleContainer.OrganelleVisualsCreated = false;
    }

    public void ApplyMicrobeMembraneType(Entity microbe, MembraneType membraneType)
    {
        if (!microbe.Has<CellProperties>())
        {
            GD.PrintErr("Can't apply new membrane type to visualization entity as it is missing a component");
            return;
        }

        ref var cellProperties = ref microbe.Get<CellProperties>();
        cellProperties.MembraneType = membraneType;

        ref var organelleContainer = ref microbe.Get<OrganelleContainer>();

        organelleContainer.OrganelleVisualsCreated = false;
    }

    public Vector3 CalculateMicrobePhotographDistance()
    {
        var microbe = GetLastMicrobeEntity();

        if (microbe == default)
            throw new InvalidOperationException("No microbe exists to operate on");

        ref var cellProperties = ref microbe.Get<CellProperties>();

        // This uses the membrane as radius is not set as the physics system doesn't run
        if (!cellProperties.IsMembraneReady())
            throw new InvalidOperationException("Microbe doesn't have a ready membrane");

#if DEBUG
        var graphical = microbe.Get<SpatialInstance>().GraphicalInstance;
        if (graphical?.GlobalPosition != Vector3.Zero)
        {
            GD.PrintErr("Photographed cell has moved or not initialized graphics");
        }
#endif

        var radius = cellProperties.CreatedMembrane!.EncompassingCircleRadius;

        if (cellProperties.IsBacteria)
            radius *= 0.5f;

        var center = Vector3.Zero;

        ref var organelles = ref microbe.Get<OrganelleContainer>();

        // Calculate cell center graphics position for more accurate photographing
        if (organelles.CreatedOrganelleVisuals is { Count: > 0 })
        {
            float squaredRadius = radius * radius;

            foreach (var pair in organelles.CreatedOrganelleVisuals)
            {
                // We don't need to account for internal organelles as they are located within the cell's radius
                if (!pair.Key.Definition.PositionedExternally)
                    continue;

                // TODO: is there another way to not need to call so many Godot data access methods here
                // Organelle positions might be usable as the visual positions are derived from them, but this requires
                // using the global translation for some reason as translation gives just 0 here and doesn't help.
                var position = pair.Value.GlobalPosition;

                // Assume that the organelle's radius is 1
                const float organelleRadius = 1.0f;
                float organelleRadiusSquared = organelleRadius * organelleRadius;
                float squaredDistance = (position - center).LengthSquared();

                if (squaredDistance < squaredRadius - 2 * organelleRadius * radius + organelleRadiusSquared)
                    continue;

                float distance = MathF.Sqrt(squaredDistance);

                var normalized = (position - center) / distance;
                var newRadius = (radius + organelleRadius + distance) * 0.5f;

                center += normalized * (newRadius - radius);
                radius = newRadius;
                squaredRadius = radius * radius;
            }
        }
        else if (organelles.CreatedOrganelleVisuals != null)
        {
            // Cell with just cytoplasm in it

#if DEBUG

            // Verify in debug mode that initialization didn't just fail for the graphics
            foreach (var organelle in organelles.Organelles!)
            {
                if (!organelle.Definition.TryGetGraphicsScene(organelle.Upgrades, out _))
                    continue;

                GD.PrintErr("Photographed a microbe with no initialized cell graphics but it should have some");
                break;
            }
#endif
        }
        else
        {
            GD.PrintErr("Photographing a microbe that didn't initialize its organelle visuals");
        }

        return new Vector3(center.X,
            PhotoStudio.CameraDistanceFromRadiusOfObject(radius * Constants.PHOTO_STUDIO_CELL_RADIUS_MULTIPLIER),
            center.Z);
    }

    public Vector3 CalculateColonyPhotographDistance()
    {
        var species = GetLastMicrobeEntity().Get<MulticellularSpeciesMember>().Species;

        Vector3 center = Vector3.Zero;

        int count = species.Cells.Count;
        for (int i = 0; i < count; ++i)
        {
            center += Hex.AxialToCartesian(species.Cells[i].Position);
        }

        center /= count;

        float maxCellDistanceSquared = 0.0f;
        float farthestCellRadius = 0.0f;

        foreach (var entity in EntitySystem)
        {
            if (!entity.Has<CellProperties>())
                continue;

            var distanceSquared = entity.Get<WorldPosition>().Position.DistanceSquaredTo(center);

            ref var cellProperties = ref entity.Get<CellProperties>();

            // This uses the membrane as radius is not set as the physics system doesn't run
            if (!cellProperties.IsMembraneReady())
                throw new InvalidOperationException("Microbe doesn't have a ready membrane");

            float radius = cellProperties.CreatedMembrane!.EncompassingCircleRadius;

            if (distanceSquared + radius * radius > maxCellDistanceSquared + farthestCellRadius * farthestCellRadius)
            {
                maxCellDistanceSquared = distanceSquared;

                farthestCellRadius = radius;
            }
        }

        return new Vector3(center.X,
            PhotoStudio.CameraDistanceFromRadiusOfObject(MathF.Sqrt(maxCellDistanceSquared) + farthestCellRadius),
            center.Z);
    }

    public override bool HasSystemsWithPendingOperations()
    {
        return microbeVisualsSystem.HasPendingOperations();
    }

    protected override void InitSystemsEarly()
    {
    }

    // This world doesn't use physics
    protected override void WaitForStartedPhysicsRun()
    {
    }

    protected override void OnStartPhysicsRunIfTime(float delta)
    {
    }

    protected override void OnProcessFixedLogic(float delta)
    {
        microbeVisualsSystem.Update(delta);
        pathBasedSceneLoader.Update(delta);
        predefinedVisualLoaderSystem.Update(delta);
        entityMaterialFetchSystem.Update(delta);
        animationControlSystem.Update(delta);

        attachedEntityPositionSystem.Update(delta);

        // colonyBindingSystem.Update(delta);

        spatialAttachSystem.Update(delta);
        spatialPositionSystem.Update(delta);

        // organelleComponentFetchSystem.Update(delta);
        // organelleTickSystem.Update(delta);

        fadeOutActionSystem.Update(delta);

        microbeRenderPrioritySystem.Update(delta);

        cellBurstEffectSystem.Update(delta);

        microbeFlashingSystem.Update(delta);
    }

    protected override void OnProcessFrameLogic(float delta)
    {
        colourAnimationSystem.Update(delta);
        microbeShaderSystem.Update(delta);
        tintColourApplyingSystem.Update(delta);
    }

    protected override void ApplyECSThreadCount(int ecsThreadsToUse)
    {
        // This system doesn't use threading
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            animationControlSystem.Dispose();
            attachedEntityPositionSystem.Dispose();
            colourAnimationSystem.Dispose();
            entityMaterialFetchSystem.Dispose();
            fadeOutActionSystem.Dispose();
            pathBasedSceneLoader.Dispose();
            predefinedVisualLoaderSystem.Dispose();
            spatialAttachSystem.Dispose();
            spatialPositionSystem.Dispose();
            cellBurstEffectSystem.Dispose();
            microbeFlashingSystem.Dispose();
            microbeRenderPrioritySystem.Dispose();
            microbeShaderSystem.Dispose();
            microbeVisualsSystem.Dispose();
            tintColourApplyingSystem.Dispose();
        }

        base.Dispose(disposing);
    }
}
