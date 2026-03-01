# Otter IK Test Project

A Unity project demonstrating a procedural IK system paired with a physically based water player controller. An otter character swims in a Crest ocean using rigidbody physics, with all limb motion driven by runtime IK — no baked animations.

**[Live Demo](https://dw218192.github.io/OtterIkTestProject/)**

## Systems

### Two-Bone IK Solver (`AdvancedTwoBoneIK`)
Analytical 2-bone solver using law of cosines. Supports per-bone rotation constraints (euler limits or single-axis hinge), pole targets for bend direction, optional stretch, and smoothed blending.

### Front Paddle Stroke
A three-stage pipeline drives the front limbs:
- **Lag points** (`DynamicIndirectIk_V2`) — inertial drag on each shoulder detects inner/outer side during turns
- **Trajectory frame** (`IKStrokeTrajectory_V2`) — parametric teardrop-ellipse evaluated in a dynamic basis derived from the lag point
- **Stroke controller** (`IKStrokeController`) — accumulates phase from body angular spin, samples the trajectory, and smooths the IK target in stable-basis-local space

### Hind Leg Kicks
- **Trajectory** (`HindPaddleTrajectoryRB`) — parametric ellipse with tilt, turn-based yaw deviation, and demand-scaled amplitude
- **Driver** (`HindPaddleDriverRB`) — priority system merging movement kicks (from the swim rhythm machine) and per-leg idle turn kicks

### Physics Movement Controller (`CrestMovementControllerRB`)
Rigidbody swim controller integrated with Crest ocean sampling. Uses a time-domain rhythm machine (Prepare → Kick → Interval) that emits kick cycle events consumed by the limb drivers. Includes buoyancy, drag, torque-based alignment, and demand-based propulsion scaling.

### Spine & Head
- `SpineDecouplerWithShadows` — runtime shadow nodes for independent spine joint manipulation
- `HeadIKGuideFromArrowRB` — positions a head look-target from the movement controller's aim direction

## Dependencies
- [Crest Ocean System](https://github.com/wave-harmonic/crest)
