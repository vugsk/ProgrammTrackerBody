using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using HelixToolkit.Wpf;
using ProgrammTrackerBody.Domain;
using ProgrammTrackerBody.ViewModels;
using NumericsQuaternion = System.Numerics.Quaternion;
using Media3DQuaternion = System.Windows.Media.Media3D.Quaternion;

namespace ProgrammTrackerBody.Views;

public partial class SkeletonTab : UserControl
{
    private SkeletonViewModel? _vm;

    // 3D scene primitives, keyed by bone for incremental updates.
    private readonly Dictionary<SkeletonBone, PipeVisual3D> _bonePipes = new();
    private readonly Dictionary<SkeletonBone, SphereVisual3D> _jointSpheres = new();
    private readonly Dictionary<SkeletonBone, CoordinateSystemVisual3D> _trackerAxes = new();
    private GridLinesVisual3D? _ground;
    private ArrowVisual3D? _frontArrow;

    // Coalesce tracker rotation bursts into one update per render frame.
    private bool _poseDirty = true;
    private bool _renderingHooked;

    // T-pose freeze: while UtcNow < _freezeUntilUtc, render a clean rest pose
    // (ignoring live tracker data) so user can visually verify after calibration
    // or when re-entering the tab.
    private DateTime _freezeUntilUtc = DateTime.MinValue;
    private bool _wasFrozen;
    private const double FreezeSeconds = 1.5;

    public SkeletonTab()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += OnIsVisibleChanged;
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.NewValue is true)
        {
            TriggerTPoseFreeze();
        }
    }

    private void TriggerTPoseFreeze()
    {
        _freezeUntilUtc = DateTime.UtcNow.AddSeconds(FreezeSeconds);
        _poseDirty = true;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_renderingHooked)
        {
            CompositionTarget.Rendering += OnRendering;
            _renderingHooked = true;
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_renderingHooked)
        {
            CompositionTarget.Rendering -= OnRendering;
            _renderingHooked = false;
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (_vm == null) return;

        var isFrozen = DateTime.UtcNow < _freezeUntilUtc;
        var transitioning = isFrozen != _wasFrozen;
        _wasFrozen = isFrozen;

        // Render only at boundary transitions (freeze start/end) or when live data
        // arrives outside of a freeze. During the freeze window, the T-pose is
        // already on-screen — no need to repaint identical frames.
        if (!transitioning && (isFrozen || !_poseDirty)) return;

        UpdatePose(forceTPose: isFrozen);

        if (!isFrozen)
        {
            _poseDirty = false;
        }
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_vm != null)
        {
            _vm.SceneInvalidated -= OnSceneInvalidated;
            _vm.CalibrationApplied -= OnCalibrationApplied;
            _vm.Trackers.CollectionChanged -= OnTrackersCollectionChanged;
            UnsubscribeAllTrackers();
        }

        _vm = e.NewValue as SkeletonViewModel;

        if (_vm != null)
        {
            _vm.SceneInvalidated += OnSceneInvalidated;
            _vm.CalibrationApplied += OnCalibrationApplied;
            _vm.Trackers.CollectionChanged += OnTrackersCollectionChanged;
            SubscribeAllTrackers();
            RebuildScene();
        }
    }

    private void OnCalibrationApplied(object? sender, EventArgs e) => TriggerTPoseFreeze();

    private void SubscribeAllTrackers()
    {
        if (_vm == null) return;
        foreach (var t in _vm.Trackers)
            t.PropertyChanged += OnTrackerChanged;
    }

    private void UnsubscribeAllTrackers()
    {
        if (_vm == null) return;
        foreach (var t in _vm.Trackers)
            t.PropertyChanged -= OnTrackerChanged;
    }

    private void OnTrackersCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems != null)
            foreach (TrackerModel t in e.NewItems)
                t.PropertyChanged += OnTrackerChanged;

        if (e.OldItems != null)
            foreach (TrackerModel t in e.OldItems)
                t.PropertyChanged -= OnTrackerChanged;

        // Tracker added/removed → axes overlay membership may change.
        RebuildScene();
    }

    private void OnTrackerChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TrackerModel.BodyPart))
        {
            // BodyPart reassignment can change which bones are tracker-driven
            // (and which axes appear in the overlay). Cheaper to rebuild than diff.
            RebuildScene();
        }
        else if (e.PropertyName == nameof(TrackerModel.DisplayRotation))
        {
            _poseDirty = true;
        }
    }

    private void OnSceneInvalidated(object? sender, EventArgs e) => RebuildScene();

    private void RebuildScene()
    {
        if (_vm == null) return;

        Viewport.Children.Clear();
        _bonePipes.Clear();
        _jointSpheres.Clear();
        _trackerAxes.Clear();

        Viewport.Children.Add(new DefaultLights());

        var h = _vm.HeightMeters;

        // Ground grid scales with figure so it always frames the avatar.
        _ground = new GridLinesVisual3D
        {
            Width = h * 1.5,
            Length = h * 1.5,
            MajorDistance = h * 0.25,
            MinorDistance = h * 0.05,
            Thickness = h * 0.002,
            Center = new Point3D(0, 0, 0),
            Normal = new Vector3D(0, 1, 0),
            Fill = new SolidColorBrush(Color.FromRgb(70, 70, 90)),
        };
        Viewport.Children.Add(_ground);

        // Forward indicator — small light-blue arrow at chest height pointing
        // along avatar's +Z axis. Helps disambiguate which way the figure faces
        // when watching from behind.
        var arrowBrush = new SolidColorBrush(Color.FromRgb(100, 180, 255));
        arrowBrush.Freeze();
        _frontArrow = new ArrowVisual3D
        {
            Point1 = new Point3D(0, 0, 0),
            Point2 = new Point3D(0, 0, h * 0.18),
            Diameter = h * 0.012,
            Fill = arrowBrush,
        };
        Viewport.Children.Add(_frontArrow);

        var boneBrush = new SolidColorBrush(Color.FromRgb(200, 200, 220));
        var jointBrush = new SolidColorBrush(Color.FromRgb(140, 140, 180));
        boneBrush.Freeze();
        jointBrush.Freeze();

        var boneDiameter = h * 0.025;
        var jointRadius = h * 0.022;

        // Build a "rest pose" pipe per bone: Point1=(0,0,0), Point2=RestDir*length.
        // Geometry is generated once; per-frame Transform handles world placement.
        foreach (var bone in SkeletonDefinition.Bones)
        {
            if (bone.LengthFraction <= 0) continue; // Hip is a hub, no rendered segment

            var length = bone.LengthFraction * h;
            var endLocal = new Point3D(
                bone.RestDir.X * length,
                bone.RestDir.Y * length,
                bone.RestDir.Z * length);

            var pipe = new PipeVisual3D
            {
                Point1 = new Point3D(0, 0, 0),
                Point2 = endLocal,
                Diameter = boneDiameter,
                InnerDiameter = 0,
                ThetaDiv = 12,
                Fill = boneBrush,
            };
            Viewport.Children.Add(pipe);
            _bonePipes[bone.Bone] = pipe;
        }

        // One sphere per bone, drawn at the bone's tip joint.
        // Center=(0,0,0) once → animate via Transform to avoid mesh rebuilds.
        foreach (var bone in SkeletonDefinition.Bones)
        {
            var sphere = new SphereVisual3D
            {
                Center = new Point3D(0, 0, 0),
                Radius = jointRadius,
                ThetaDiv = 14,
                PhiDiv = 10,
                Fill = jointBrush,
            };
            Viewport.Children.Add(sphere);
            _jointSpheres[bone.Bone] = sphere;
        }

        // Optional debug overlay: RGB axes at every tracker-driven bone.
        if (_vm.ShowTrackerAxes)
        {
            var assigned = new HashSet<BodyPart>();
            foreach (var t in _vm.Trackers)
                if (t.BodyPart != BodyPart.None)
                    assigned.Add(t.BodyPart);

            foreach (var bone in SkeletonDefinition.Bones)
            {
                if (bone.Controller is { } ctrl && assigned.Contains(ctrl))
                {
                    var axes = new CoordinateSystemVisual3D
                    {
                        ArrowLengths = h * 0.06,
                    };
                    Viewport.Children.Add(axes);
                    _trackerAxes[bone.Bone] = axes;
                }
            }
        }

        _poseDirty = true;
    }

    private void UpdatePose(bool forceTPose)
    {
        if (_vm == null) return;

        var rotations = new Dictionary<BodyPart, NumericsQuaternion>();
        if (!forceTPose)
        {
            foreach (var t in _vm.Trackers)
            {
                if (t.BodyPart != BodyPart.None)
                    rotations[t.BodyPart] = t.DisplayRotation;
            }
        }
        // When forceTPose, rotations stays empty → SkeletonPose.Compute falls back
        // to identity rotations on every bone (parent inheritance from null root),
        // producing a clean rest pose.

        var pose = SkeletonPose.Compute(rotations, _vm.HeightMeters);

        // Bones: rotate local rest-pose pipe by world rotation, translate to head.
        foreach (var (boneId, pipe) in _bonePipes)
        {
            var head = pose.HeadPositions[boneId];
            var rot = pose.WorldRots[boneId];
            pipe.Transform = ToTransform(head, rot);
        }

        // Joints: position each sphere at the bone's tip.
        foreach (var (boneId, sphere) in _jointSpheres)
        {
            var tip = pose.TipPositions[boneId];
            sphere.Transform = new TranslateTransform3D(tip.X, tip.Y, tip.Z);
        }

        // Axes: position at the tracker-driven bone's head, oriented by world rotation.
        foreach (var (boneId, axes) in _trackerAxes)
        {
            var head = pose.HeadPositions[boneId];
            var rot = pose.WorldRots[boneId];
            axes.Transform = ToTransform(head, rot);
        }

        // Forward indicator: at chest position, oriented by chest rotation
        // (so the arrow turns when the user twists their torso).
        if (_frontArrow is not null && pose.HeadPositions.TryGetValue(SkeletonBone.UpperChest, out var chestPos))
        {
            var chestRot = pose.WorldRots.TryGetValue(SkeletonBone.UpperChest, out var cr)
                ? cr
                : pose.WorldRots[SkeletonBone.Hip];
            _frontArrow.Transform = ToTransform(chestPos, chestRot);
        }
    }

    private static Transform3D ToTransform(Vector3 position, NumericsQuaternion q)
    {
        var group = new Transform3DGroup();
        var wpfQ = new Media3DQuaternion(q.X, q.Y, q.Z, q.W);
        group.Children.Add(new RotateTransform3D(new QuaternionRotation3D(wpfQ)));
        group.Children.Add(new TranslateTransform3D(position.X, position.Y, position.Z));
        return group;
    }
}
