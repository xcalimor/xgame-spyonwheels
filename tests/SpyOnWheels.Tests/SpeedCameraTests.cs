using System;
using Microsoft.Xna.Framework;
using SpyOnWheels;
using Xunit;

namespace SpyOnWheels.Tests;

/// <summary>WI-017: the camera zooms out with speed and eases, never jumps.</summary>
public class SpeedCameraTests
{
    private const double Dt = 1 / 60.0;

    [Fact]
    public void Standing_still_is_the_near_zoom()
    {
        var cam = new SpeedCamera(Car.TopSpeedMs);
        for (int i = 0; i < 120; i++) cam.Update(0, Dt);
        Assert.Equal(SpeedCamera.NearZoom, cam.Zoom, 4);
    }

    [Fact]
    public void At_top_speed_the_view_is_twice_as_wide()
    {
        var cam = new SpeedCamera(Car.TopSpeedMs);
        for (int i = 0; i < 600; i++) cam.Update(Car.TopSpeedMs, Dt);
        Assert.Equal(SpeedCamera.NearZoom / SpeedCamera.WidenAtTopSpeed, cam.Zoom, 3);
    }

    [Fact]
    public void Faster_is_always_wider()
    {
        var cam = new SpeedCamera(Car.TopSpeedMs);
        float last = float.MaxValue;
        for (double v = 0; v <= Car.TopSpeedMs; v += 2)
        {
            float z = cam.TargetAt(v);
            Assert.True(z < last);
            last = z;
        }
        Assert.Equal(cam.TargetAt(5), cam.TargetAt(-5));   // backing up widens it the same
    }

    [Fact]
    public void A_crash_from_top_speed_eases_back_in_never_snaps()
    {
        var cam = new SpeedCamera(Car.TopSpeedMs);
        for (int i = 0; i < 600; i++) cam.Update(Car.TopSpeedMs, Dt);
        float before = cam.Zoom;
        cam.Update(0, Dt);   // stopped dead in one step
        Assert.True(cam.Zoom - before < (SpeedCamera.NearZoom - before) * 0.2f, $"one frame moved {cam.Zoom - before:0.0000}");
        for (int i = 0; i < 60; i++) cam.Update(0, Dt);
        Assert.Equal(SpeedCamera.NearZoom, cam.Zoom, 2);   // back within 1 s
    }

    private static readonly Vector2 View = new(1280, 720);
    private static readonly Vector2 North = new(0, -1), East = new(1, 0);

    private static SpeedCamera Settled(double speedMs, Vector2 forward)
    {
        var cam = new SpeedCamera(Car.TopSpeedMs);
        for (int i = 0; i < 600; i++) cam.Update(speedMs, Dt, forward, View);
        return cam;
    }

    [Fact]
    public void Standing_still_the_camera_is_on_the_car()
    {
        Assert.True(Settled(0, North).Lead.Length() < 0.01f);
    }

    [Fact]
    public void At_top_speed_the_car_sits_halfway_to_the_trailing_edge()
    {
        var north = Settled(Car.TopSpeedMs, North);
        float halfH = View.Y / 2 / north.Zoom;
        Assert.Equal(-halfH * SpeedCamera.LeadAtTopSpeed, north.Lead.Y, 1);   // ahead is up the screen
        Assert.Equal(0, north.Lead.X, 1);
        var east = Settled(Car.TopSpeedMs, East);
        Assert.Equal(View.X / 2 / east.Zoom * SpeedCamera.LeadAtTopSpeed, east.Lead.X, 1);   // the wide axis shows more ahead
    }

    [Fact]
    public void Backing_up_leads_backward()
    {
        Assert.True(Settled(-Car.ReverseSpeedMs, North).Lead.Y > 0);
    }

    [Fact]
    public void A_crash_eases_the_lead_back_never_snaps()
    {
        var cam = Settled(Car.TopSpeedMs, North);
        float before = cam.Lead.Length();
        cam.Update(0, Dt, North, View);
        Assert.True(before - cam.Lead.Length() < before * 0.2f);
        for (int i = 0; i < 90; i++) cam.Update(0, Dt, North, View);
        Assert.True(cam.Lead.Length() < before * 0.02f);   // gone within 1.5 s
    }
}
