﻿/*****************************************************************************
 * RasterPropMonitor
 * =================
 * Plugin for Kerbal Space Program
 *
 *  by Mihara (Eugene Medvedev), MOARdV, and other contributors
 * 
 * RasterPropMonitor is free software: you can redistribute it and/or modify
 * it under the terms of the GNU General Public License as published by
 * the Free Software Foundation, either version 3 of the License, revision
 * date 29 June 2007, or (at your option) any later version.
 * 
 * RasterPropMonitor is distributed in the hope that it will be useful, but
 * WITHOUT ANY WARRANTY; without even the implied warranty of MERCHANTABILITY
 * or FITNESS FOR A PARTICULAR PURPOSE.  See the GNU General Public License
 * for more details.
 * 
 * You should have received a copy of the GNU General Public License
 * along with RasterPropMonitor.  If not, see <http://www.gnu.org/licenses/>.
 ****************************************************************************/
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace JSI
{
    public partial class RPMVesselComputer : VesselModule
    {
        private const float KelvinToCelsius = -273.15f;
        internal const float MetersToFeet = 3.2808399f;
        internal const float MetersPerSecondToKnots = 1.94384449f;
        internal const float MetersPerSecondToFeetPerMinute = 196.850394f;

        //--- The guts of the variable processor
        #region VariableToObject
        /// <summary>
        /// The core of the variable processor.
        /// </summary>
        /// <param name="input"></param>
        /// <param name="propId"></param>
        /// <param name="cacheable"></param>
        /// <returns></returns>
        private object VariableToObject(string input, int propId, out bool cacheable)
        {
            // Some variables may not cacheable, because they're meant to be different every time like RANDOM,
            // or immediate. they will set this flag to false.
            cacheable = true;

            // It's slightly more optimal if we take care of that before the main switch body.
            if (input.IndexOf("_", StringComparison.Ordinal) > -1)
            {
                string[] tokens = input.Split('_');

                // If input starts with ISLOADED, this is a query on whether a specific DLL has been loaded into the system.
                // So we look it up in our list.
                if (tokens.Length == 2 && tokens[0] == "ISLOADED")
                {
                    return knownLoadedAssemblies.Contains(tokens[1]) ? 1d : 0d;
                }

                // If input starts with SYSR, this is a named system resource which we should recognise and return.
                // The qualifier rules did not change since individually named resources got deprecated.
                if (tokens.Length == 2 && tokens[0] == "SYSR")
                {
                    foreach (KeyValuePair<string, string> resourceType in RPMVesselComputer.systemNamedResources)
                    {
                        if (tokens[1].StartsWith(resourceType.Key, StringComparison.Ordinal))
                        {
                            string argument = tokens[1].Substring(resourceType.Key.Length);
                            if (argument.StartsWith("STAGE", StringComparison.Ordinal))
                            {
                                argument = argument.Substring("STAGE".Length);
                                return resources.ListElement(resourceType.Value, argument, true);
                            }
                            return resources.ListElement(resourceType.Value, argument, false);
                        }
                    }
                }

                // If input starts with "LISTR" we're handling it specially -- it's a list of all resources.
                // The variables are named like LISTR_<number>_<NAME|VAL|MAX>
                if (tokens.Length == 3 && tokens[0] == "LISTR")
                {
                    ushort resourceID = Convert.ToUInt16(tokens[1]);
                    if (tokens[2] == "NAME")
                    {
                        return resourceID >= resourcesAlphabetic.Length ? string.Empty : resourcesAlphabetic[resourceID];
                    }
                    if (resourceID >= resourcesAlphabetic.Length)
                        return 0d;
                    return tokens[2].StartsWith("STAGE", StringComparison.Ordinal) ?
                        resources.ListElement(resourcesAlphabetic[resourceID], tokens[2].Substring("STAGE".Length), true) :
                        resources.ListElement(resourcesAlphabetic[resourceID], tokens[2], false);
                }

                // Periodic variables - A value that toggles between 0 and 1 with
                // the specified (game clock) period.
                if (tokens.Length > 1 && tokens[0] == "PERIOD")
                {
                    if (tokens[1].Substring(tokens[1].Length - 2) == "HZ")
                    {
                        double period;
                        if (double.TryParse(tokens[1].Substring(0, tokens[1].Length - 2), out period) && period > 0.0)
                        {
                            double invPeriod = 1.0 / period;

                            double remainder = Planetarium.GetUniversalTime() % invPeriod;

                            return (remainder > invPeriod * 0.5).GetHashCode();
                        }
                    }

                    return input;
                }

                // Custom variables - if the first token is CUSTOM, we'll evaluate it here
                if (tokens.Length > 1 && tokens[0] == "CUSTOM")
                {
                    if (customVariables.ContainsKey(input))
                    {
                        return customVariables[input].Evaluate(this);
                    }
                    else
                    {
                        return input;
                    }
                }

                // Mapped variables - if the first token is MAPPED, we'll evaluate it here
                if (tokens.Length > 1 && tokens[0] == "MAPPED")
                {
                    if (mappedVariables.ContainsKey(input))
                    {
                        return mappedVariables[input].Evaluate(this);
                    }
                    else
                    {
                        return input;
                    }
                }

                // Mapped variables - if the first token is MATH, we'll evaluate it here
                if (tokens.Length > 1 && tokens[0] == "MATH")
                {
                    if (mathVariables.ContainsKey(input))
                    {
                        return mathVariables[input].Evaluate(this);
                    }
                    else
                    {
                        return input;
                    }
                }

                // Plugin variables.  Let's get crazy!
                if (tokens.Length == 2 && tokens[0] == "PLUGIN")
                {
                    if (pluginVariables.ContainsKey(tokens[1]))
                    {
                        return pluginVariables[tokens[1]].Evaluate(vessel);
                    }
                    else if (pluginBoolVariables.ContainsKey(tokens[1]))
                    {
                        Func<bool> pluginCall = pluginBoolVariables[tokens[1]];
                        if (pluginCall != null)
                        {
                            return pluginCall().GetHashCode();
                        }
                        else
                        {
                            return -1;
                        }
                    }
                    else if (pluginDoubleVariables.ContainsKey(tokens[1]))
                    {
                        Func<double> pluginCall = pluginDoubleVariables[tokens[1]];
                        if (pluginCall != null)
                        {
                            return pluginCall();
                        }
                        else
                        {
                            return double.NaN;
                        }
                    }
                    else
                    {
                        PluginEvaluator pluginMethod = GetInternalMethod(tokens[1]);
                        if (pluginMethod != null)
                        {
                            JUtil.LogMessage(this, "Adding {0} as a PluginEvaluator", tokens[1]);
                            pluginVariables.Add(tokens[1], pluginMethod);

                            return pluginMethod.Evaluate(vessel);
                        }

                        string[] internalModule = tokens[1].Split(':');
                        if (internalModule.Length != 2)
                        {
                            JUtil.LogErrorMessage(this, "Badly-formed plugin name in {0}", input);
                            return input;
                        }

                        InternalProp propToUse = null;
                        if (part != null)
                        {
                            if (propId >= 0 && propId < part.internalModel.props.Count)
                            {
                                propToUse = part.internalModel.props[propId];
                            }
                            else
                            {
                                foreach (InternalProp thisProp in part.internalModel.props)
                                {
                                    foreach (InternalModule module in thisProp.internalModules)
                                    {
                                        if (module != null && module.ClassName == internalModule[0])
                                        {
                                            propToUse = thisProp;
                                            break;
                                        }
                                    }
                                }
                            }
                        }

                        if (propToUse == null)
                        {
                            JUtil.LogErrorMessage(this, "Tried to look for method with propToUse still null?");
                            pluginBoolVariables.Add(tokens[1], null);
                            return -1;
                        }
                        else
                        {
                            Func<bool> pluginCall = (Func<bool>)JUtil.GetMethod(tokens[1], propToUse, typeof(Func<bool>));
                            if (pluginCall == null)
                            {
                                Func<double> pluginNumericCall = (Func<double>)JUtil.GetMethod(tokens[1], propToUse, typeof(Func<double>));
                                if (pluginNumericCall != null)
                                {
                                    JUtil.LogMessage(this, "Adding {0} as a Func<double>", tokens[1]);
                                    pluginDoubleVariables.Add(tokens[1], pluginNumericCall);
                                    return pluginNumericCall();
                                }
                                else
                                {
                                    // Doesn't exist -- return nothing
                                    pluginBoolVariables.Add(tokens[1], null);
                                    return -1;
                                }
                            }
                            else
                            {
                                JUtil.LogMessage(this, "Adding {0} as a Func<bool>", tokens[1]);
                                pluginBoolVariables.Add(tokens[1], pluginCall);

                                return pluginCall().GetHashCode();
                            }
                        }
                    }
                }

                if (tokens.Length > 1 && tokens[0] == "PROP")
                {
                    string substr = input.Substring("PROP".Length + 1);

                    if (rpmComp != null && rpmComp.HasPropVar(substr, propId))
                    {
                        // Can't cache - multiple props could call in here.
                        cacheable = false;
                        return (float)rpmComp.GetPropVar(substr, propId);
                    }
                    else
                    {
                        return input;
                    }
                }

                if (tokens.Length > 1 && tokens[0] == "PERSISTENT")
                {
                    string substr = input.Substring("PERSISTENT".Length + 1);

                    if (rpmComp != null && rpmComp.HasVar(substr))
                    {
                        // MOARdV TODO: Can this be cacheable?  Should only have one
                        // active part at a time, so I think it's safe.
                        return (float)rpmComp.GetVar(substr);
                    }
                    else
                    {
                        return -1.0f;
                    }
                }

                // We do similar things for crew rosters.
                // The syntax is therefore CREW_<index>_<FIRST|LAST|FULL>
                // Part-local crew list is identical but CREWLOCAL_.
                if (tokens.Length == 3)
                {
                    ushort crewSeatID = Convert.ToUInt16(tokens[1]);
                    switch (tokens[0])
                    {
                        case "CREW":
                            return CrewListElement(tokens[2], crewSeatID, vesselCrew, vesselCrewMedical);
                        case "CREWLOCAL":
                            return CrewListElement(tokens[2], crewSeatID, localCrew, localCrewMedical);
                    }
                }

                // Strings stored in module configuration.
                if (tokens.Length == 2 && tokens[0] == "STOREDSTRING")
                {
                    int storedStringNumber;
                    if (rpmComp != null && int.TryParse(tokens[1], out storedStringNumber) && storedStringNumber >= 0)
                    {
                        return rpmComp.GetStoredString(storedStringNumber);
                    }
                    return "";
                }
            }

            if (input.StartsWith("AGMEMO", StringComparison.Ordinal))
            {
                uint groupID;
                if (uint.TryParse(input.Substring(6), out groupID) && groupID < 10)
                {
                    string[] tokens;
                    if (actionGroupMemo[groupID].IndexOf('|') > 1 && (tokens = actionGroupMemo[groupID].Split('|')).Length == 2)
                    {
                        if (vessel.ActionGroups.groups[RPMVesselComputer.actionGroupID[groupID]])
                            return tokens[0];
                        return tokens[1];
                    }
                    return actionGroupMemo[groupID];
                }
                return input;
            }
            // Action group state.
            if (input.StartsWith("AGSTATE", StringComparison.Ordinal))
            {
                uint groupID;
                if (uint.TryParse(input.Substring(7), out groupID) && groupID < 10)
                {
                    return (vessel.ActionGroups.groups[actionGroupID[groupID]]).GetHashCode();
                }
                return input;
            }

            switch (input)
            {

                // It's a bit crude, but it's simple enough to populate.
                // Would be a bit smoother if I had eval() :)

                // Speeds.
                case "VERTSPEED":
                    return speedVertical;
                case "VERTSPEEDLOG10":
                    return JUtil.PseudoLog10(speedVertical);
                case "VERTSPEEDROUNDED":
                    return speedVerticalRounded;
                case "TERMINALVELOCITY":
                    return TerminalVelocity();
                case "SURFSPEED":
                    return vessel.srfSpeed;
                case "SURFSPEEDMACH":
                    // Mach number wiggles around 1e-7 when sitting in launch
                    // clamps before launch, so pull it down to zero if it's close.
                    return (vessel.mach < 0.001) ? 0.0 : vessel.mach;
                case "ORBTSPEED":
                    return vessel.orbit.GetVel().magnitude;
                case "TRGTSPEED":
                    return velocityRelativeTarget.magnitude;
                case "HORZVELOCITY":
                    return speedHorizontal;
                case "HORZVELOCITYFORWARD":
                    // Negate it, since this is actually movement on the Z axis,
                    // and we want to treat it as a 2D projection on the surface
                    // such that moving "forward" has a positive value.
                    return -Vector3d.Dot(vessel.srf_velocity, surfaceForward);
                case "HORZVELOCITYRIGHT":
                    return Vector3d.Dot(vessel.srf_velocity, surfaceRight);
                case "EASPEED":
                    return vessel.srfSpeed * Math.Sqrt(vessel.atmDensity / standardAtmosphere);
                case "APPROACHSPEED":
                    return approachSpeed;
                case "SELECTEDSPEED":
                    switch (FlightUIController.speedDisplayMode)
                    {
                        case FlightUIController.SpeedDisplayModes.Orbit:
                            return vessel.orbit.GetVel().magnitude;
                        case FlightUIController.SpeedDisplayModes.Surface:
                            return vessel.srfSpeed;
                        case FlightUIController.SpeedDisplayModes.Target:
                            return velocityRelativeTarget.magnitude;
                    }
                    return double.NaN;


                case "TGTRELX":
                    if (target != null)
                    {
                        return Vector3d.Dot(FlightGlobals.ship_tgtVelocity, FlightGlobals.ActiveVessel.ReferenceTransform.right);
                    }
                    else
                    {
                        return 0.0;
                    }

                case "TGTRELY":
                    if (target != null)
                    {
                        return Vector3d.Dot(FlightGlobals.ship_tgtVelocity, FlightGlobals.ActiveVessel.ReferenceTransform.forward);
                    }
                    else
                    {
                        return 0.0;
                    }
                case "TGTRELZ":
                    if (target != null)
                    {
                        return Vector3d.Dot(FlightGlobals.ship_tgtVelocity, FlightGlobals.ActiveVessel.ReferenceTransform.up);
                    }
                    else
                    {
                        return 0.0;
                    }

                // Time to impact. This is quite imprecise, because a precise calculation pulls in pages upon pages of MechJeb code.
                // It accounts for gravity now, though. Pull requests welcome.
                case "TIMETOIMPACTSECS":
                    {
                        double secondsToImpact = EstimateSecondsToImpact();
                        return (double.IsNaN(secondsToImpact) || secondsToImpact > 365.0 * 24.0 * 60.0 * 60.0 || secondsToImpact < 0.0) ? -1.0 : secondsToImpact;
                    }
                case "SPEEDATIMPACT":
                    return SpeedAtImpact(totalCurrentThrust);
                case "BESTSPEEDATIMPACT":
                    return SpeedAtImpact(totalMaximumThrust);
                case "SUICIDEBURNSTARTSECS":
                    if (vessel.orbit.PeA > 0.0)
                    {
                        return double.NaN;
                    }
                    return SuicideBurnCountdown();

                case "LATERALBRAKEDISTANCE":
                    // (-(SHIP:SURFACESPEED)^2)/(2*(ship:maxthrust/ship:mass)) 
                    if (totalMaximumThrust <= 0.0)
                    {
                        // It should be impossible for wet mass to be zero.
                        return -1.0;
                    }
                    return (speedHorizontal * speedHorizontal) / (2.0 * totalMaximumThrust / totalShipWetMass);

                // Altitudes
                case "ALTITUDE":
                    return altitudeASL;
                case "ALTITUDELOG10":
                    return JUtil.PseudoLog10(altitudeASL);
                case "RADARALT":
                    return altitudeTrue;
                case "RADARALTLOG10":
                    return JUtil.PseudoLog10(altitudeTrue);
                case "RADARALTOCEAN":
                    if (vessel.mainBody.ocean)
                    {
                        return Math.Min(altitudeASL, altitudeTrue);
                    }
                    return altitudeTrue;
                case "RADARALTOCEANLOG10":
                    if (vessel.mainBody.ocean)
                    {
                        return JUtil.PseudoLog10(Math.Min(altitudeASL, altitudeTrue));
                    }
                    return JUtil.PseudoLog10(altitudeTrue);
                case "ALTITUDEBOTTOM":
                    return altitudeBottom;
                case "ALTITUDEBOTTOMLOG10":
                    return JUtil.PseudoLog10(altitudeBottom);
                case "TERRAINHEIGHT":
                    return vessel.terrainAltitude;
                case "TERRAINDELTA":
                    return terrainDelta;
                case "TERRAINHEIGHTLOG10":
                    return JUtil.PseudoLog10(vessel.terrainAltitude);
                case "DISTTOATMOSPHERETOP":
                    return vessel.orbit.referenceBody.atmosphereDepth - altitudeASL;

                // Atmospheric values
                case "ATMPRESSURE":
                    return vessel.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres;
                case "ATMDENSITY":
                    return vessel.atmDensity;
                case "DYNAMICPRESSURE":
                    return DynamicPressure();
                case "ATMOSPHEREDEPTH":
                    if (vessel.mainBody.atmosphere)
                    {
                        return ((upperAtmosphereLimit + Math.Log(FlightGlobals.getAtmDensity(vessel.staticPressurekPa * PhysicsGlobals.KpaToAtmospheres, FlightGlobals.Bodies[1].atmosphereTemperatureSeaLevel) /
                        FlightGlobals.getAtmDensity(FlightGlobals.currentMainBody.atmospherePressureSeaLevel, FlightGlobals.currentMainBody.atmosphereTemperatureSeaLevel))) / upperAtmosphereLimit).Clamp(0d, 1d);
                    }
                    return 0d;

                // Masses.
                case "MASSDRY":
                    return totalShipDryMass;
                case "MASSWET":
                    return totalShipWetMass;
                case "MASSRESOURCES":
                    return totalShipWetMass - totalShipDryMass;
                case "MASSPROPELLANT":
                    return resources.PropellantMass(false);
                case "MASSPROPELLANTSTAGE":
                    return resources.PropellantMass(true);

                // The delta V calculation.
                case "DELTAV":
                    return DeltaV();
                case "DELTAVSTAGE":
                    return DeltaVStage();

                // Thrust and related
                case "THRUST":
                    return (double)totalCurrentThrust;
                case "THRUSTMAX":
                    return (double)totalMaximumThrust;
                case "TWR":
                    return (double)(totalCurrentThrust / (totalShipWetMass * localGeeASL));
                case "TWRMAX":
                    return (double)(totalMaximumThrust / (totalShipWetMass * localGeeASL));
                case "ACCEL":
                    return (double)(totalCurrentThrust / totalShipWetMass);
                case "MAXACCEL":
                    return (double)(totalMaximumThrust / totalShipWetMass);
                case "GFORCE":
                    return vessel.geeForce_immediate;
                case "EFFECTIVEACCEL":
                    return vessel.acceleration.magnitude;
                case "REALISP":
                    return (double)actualAverageIsp;
                case "HOVERPOINT":
                    return (double)(localGeeDirect / (totalMaximumThrust / totalShipWetMass)).Clamp(0.0f, 1.0f);
                case "HOVERPOINTEXISTS":
                    return ((localGeeDirect / (totalMaximumThrust / totalShipWetMass)) > 1.0f) ? -1.0 : 1.0;
                case "EFFECTIVETHROTTLE":
                    return (totalMaximumThrust > 0.0f) ? (double)(totalCurrentThrust / totalMaximumThrust) : 0.0;
                case "DRAG":
                    return DragForce();
                case "DRAGACCEL":
                    return DragForce() / totalShipWetMass;
                case "LIFT":
                    return LiftForce();
                case "LIFTACCEL":
                    return LiftForce() / totalShipWetMass;

                // Maneuvers
                case "MNODETIMESECS":
                    if (node != null)
                    {
                        return -(node.UT - Planetarium.GetUniversalTime());
                    }
                    return double.NaN;
                case "MNODEDV":
                    if (node != null)
                    {
                        return node.GetBurnVector(vessel.orbit).magnitude;
                    }
                    return 0d;
                case "MNODEBURNTIMESECS":
                    if (node != null && totalMaximumThrust > 0 && actualAverageIsp > 0)
                    {
                        return actualAverageIsp * (1 - Math.Exp(-node.GetBurnVector(vessel.orbit).magnitude / actualAverageIsp / gee)) / (totalMaximumThrust / (totalShipWetMass * gee));
                    }
                    return double.NaN;
                case "MNODEEXISTS":
                    return node == null ? -1d : 1d;


                // Orbital parameters
                case "ORBITBODY":
                    return vessel.orbit.referenceBody.name;
                case "PERIAPSIS":
                    if (orbitSensibility)
                        return vessel.orbit.PeA;
                    return double.NaN;
                case "APOAPSIS":
                    if (orbitSensibility)
                    {
                        return vessel.orbit.ApA;
                    }
                    return double.NaN;
                case "INCLINATION":
                    if (orbitSensibility)
                    {
                        return vessel.orbit.inclination;
                    }
                    return double.NaN;
                case "ECCENTRICITY":
                    if (orbitSensibility)
                    {
                        return vessel.orbit.eccentricity;
                    }
                    return double.NaN;
                case "SEMIMAJORAXIS":
                    if (orbitSensibility)
                    {
                        return vessel.orbit.semiMajorAxis;
                    }
                    return double.NaN;

                case "ORBPERIODSECS":
                    if (orbitSensibility)
                        return vessel.orbit.period;
                    return double.NaN;
                case "TIMETOAPSECS":
                    if (orbitSensibility)
                        return vessel.orbit.timeToAp;
                    return double.NaN;
                case "TIMETOPESECS":
                    if (orbitSensibility)
                        return vessel.orbit.eccentricity < 1 ?
                            vessel.orbit.timeToPe :
                            -vessel.orbit.meanAnomaly / (2 * Math.PI / vessel.orbit.period);
                    return double.NaN;
                case "TIMESINCELASTAP":
                    if (orbitSensibility)
                        return vessel.orbit.period - vessel.orbit.timeToAp;
                    return double.NaN;
                case "TIMESINCELASTPE":
                    if (orbitSensibility)
                        return vessel.orbit.period - (vessel.orbit.eccentricity < 1 ? vessel.orbit.timeToPe : -vessel.orbit.meanAnomaly / (2 * Math.PI / vessel.orbit.period));
                    return double.NaN;
                case "TIMETONEXTAPSIS":
                    if (orbitSensibility)
                    {
                        double apsisType = NextApsisType();
                        if (apsisType < 0.0)
                        {
                            return vessel.orbit.eccentricity < 1 ?
                                vessel.orbit.timeToPe :
                                -vessel.orbit.meanAnomaly / (2 * Math.PI / vessel.orbit.period);
                        }
                        return vessel.orbit.timeToAp;
                    }
                    return double.NaN;
                case "NEXTAPSIS":
                    if (orbitSensibility)
                    {
                        double apsisType = NextApsisType();
                        if (apsisType < 0.0)
                        {
                            return vessel.orbit.PeA;
                        }
                        if (apsisType > 0.0)
                        {
                            return vessel.orbit.ApA;
                        }
                    }
                    return double.NaN;
                case "NEXTAPSISTYPE":
                    return NextApsisType();
                case "ORBITMAKESSENSE":
                    if (orbitSensibility)
                        return 1d;
                    return -1d;
                case "TIMETOANEQUATORIAL":
                    if (orbitSensibility && vessel.orbit.AscendingNodeEquatorialExists())
                        return vessel.orbit.TimeOfAscendingNodeEquatorial(Planetarium.GetUniversalTime()) - Planetarium.GetUniversalTime();
                    return double.NaN;
                case "TIMETODNEQUATORIAL":
                    if (orbitSensibility && vessel.orbit.DescendingNodeEquatorialExists())
                        return vessel.orbit.TimeOfDescendingNodeEquatorial(Planetarium.GetUniversalTime()) - Planetarium.GetUniversalTime();
                    return double.NaN;

                // SOI changes in orbits.
                case "ENCOUNTEREXISTS":
                    if (orbitSensibility)
                    {
                        switch (vessel.orbit.patchEndTransition)
                        {
                            case Orbit.PatchTransitionType.ESCAPE:
                                return -1d;
                            case Orbit.PatchTransitionType.ENCOUNTER:
                                return 1d;
                        }
                    }
                    return 0d;
                case "ENCOUNTERTIME":
                    if (orbitSensibility &&
                        (vessel.orbit.patchEndTransition == Orbit.PatchTransitionType.ENCOUNTER ||
                        vessel.orbit.patchEndTransition == Orbit.PatchTransitionType.ESCAPE))
                    {
                        return vessel.orbit.UTsoi - Planetarium.GetUniversalTime();
                    }
                    return double.NaN;
                case "ENCOUNTERBODY":
                    if (orbitSensibility)
                    {
                        switch (vessel.orbit.patchEndTransition)
                        {
                            case Orbit.PatchTransitionType.ENCOUNTER:
                                return vessel.orbit.nextPatch.referenceBody.bodyName;
                            case Orbit.PatchTransitionType.ESCAPE:
                                return vessel.mainBody.referenceBody.bodyName;
                        }
                    }
                    return string.Empty;

                // Time
                case "UTSECS":
                    if (GameSettings.KERBIN_TIME)
                    {
                        return Planetarium.GetUniversalTime() + 426 * 6 * 60 * 60;
                    }
                    return Planetarium.GetUniversalTime() + 365 * 24 * 60 * 60;
                case "METSECS":
                    return vessel.missionTime;

                // Names!
                case "NAME":
                    return vessel.vesselName;
                case "VESSELTYPE":
                    return vessel.vesselType.ToString();
                case "TARGETTYPE":
                    if (targetVessel != null)
                    {
                        return targetVessel.vesselType.ToString();
                    }
                    if (targetDockingNode != null)
                    {
                        return "Port";
                    }
                    if (targetBody != null)
                    {
                        return "Celestial";
                    }
                    return "Position";

                // Coordinates.
                case "LATITUDE":
                    return vessel.mainBody.GetLatitude(CoM);
                case "LONGITUDE":
                    return JUtil.ClampDegrees180(vessel.mainBody.GetLongitude(CoM));
                case "TARGETLATITUDE":
                case "LATITUDETGT":
                    // These targetables definitely don't have any coordinates.
                    if (target == null || target is CelestialBody)
                    {
                        return double.NaN;
                    }
                    // These definitely do.
                    if (target is Vessel || target is ModuleDockingNode)
                    {
                        return target.GetVessel().mainBody.GetLatitude(target.GetTransform().position);
                    }
                    // We're going to take a guess here and expect MechJeb's PositionTarget and DirectionTarget,
                    // which don't have vessel structures but do have a transform.
                    return vessel.mainBody.GetLatitude(target.GetTransform().position);
                case "TARGETLONGITUDE":
                case "LONGITUDETGT":
                    if (target == null || target is CelestialBody)
                    {
                        return double.NaN;
                    }
                    if (target is Vessel || target is ModuleDockingNode)
                    {
                        return JUtil.ClampDegrees180(target.GetVessel().mainBody.GetLongitude(target.GetTransform().position));
                    }
                    return vessel.mainBody.GetLongitude(target.GetTransform().position);

                // Orientation
                case "HEADING":
                    return rotationVesselSurface.eulerAngles.y;
                case "PITCH":
                    return (rotationVesselSurface.eulerAngles.x > 180) ? (360.0 - rotationVesselSurface.eulerAngles.x) : -rotationVesselSurface.eulerAngles.x;
                case "ROLL":
                    return (rotationVesselSurface.eulerAngles.z > 180) ? (360.0 - rotationVesselSurface.eulerAngles.z) : -rotationVesselSurface.eulerAngles.z;
                case "ANGLEOFATTACK":
                    return AngleOfAttack();
                case "SIDESLIP":
                    return SideSlip();
                // These values get odd when they're way out on the edge of the
                // navball because they're projected into two dimensions.
                case "PITCHPROGRADE":
                    return GetRelativePitch(prograde);
                case "PITCHRETROGRADE":
                    return GetRelativePitch(-prograde);
                case "PITCHRADIALIN":
                    return GetRelativePitch(-radialOut);
                case "PITCHRADIALOUT":
                    return GetRelativePitch(radialOut);
                case "PITCHNORMALPLUS":
                    return GetRelativePitch(normalPlus);
                case "PITCHNORMALMINUS":
                    return GetRelativePitch(-normalPlus);
                case "PITCHNODE":
                    if (node != null)
                    {
                        return GetRelativePitch(node.GetBurnVector(vessel.orbit).normalized);
                    }
                    else
                    {
                        return 0.0;
                    }
                case "PITCHTARGET":
                    if (target != null)
                    {
                        return GetRelativePitch(-targetSeparation.normalized);
                    }
                    else
                    {
                        return 0.0;
                    }
                case "YAWPROGRADE":
                    return GetRelativeYaw(prograde);
                case "YAWRETROGRADE":
                    return GetRelativeYaw(-prograde);
                case "YAWRADIALIN":
                    return GetRelativeYaw(-radialOut);
                case "YAWRADIALOUT":
                    return GetRelativeYaw(radialOut);
                case "YAWNORMALPLUS":
                    return GetRelativeYaw(normalPlus);
                case "YAWNORMALMINUS":
                    return GetRelativeYaw(-normalPlus);
                case "YAWNODE":
                    if (node != null)
                    {
                        return GetRelativeYaw(node.GetBurnVector(vessel.orbit).normalized);
                    }
                    else
                    {
                        return 0.0;
                    }
                case "YAWTARGET":
                    if (target != null)
                    {
                        return GetRelativeYaw(-targetSeparation.normalized);
                    }
                    else
                    {
                        return 0.0;
                    }

                // Targeting. Probably the most finicky bit right now.
                case "TARGETNAME":
                    if (target == null)
                        return string.Empty;
                    if (target is Vessel || target is CelestialBody || target is ModuleDockingNode)
                        return target.GetName();
                    // What remains is MechJeb's ITargetable implementations, which also can return a name,
                    // but the newline they return in some cases needs to be removed.
                    return target.GetName().Replace('\n', ' ');
                case "TARGETDISTANCE":
                    if (target != null)
                        return targetDistance;
                    return -1d;
                case "TARGETGROUNDDISTANCE":
                    if (target != null)
                    {
                        Vector3d targetGroundPos = target.ProjectPositionOntoSurface(vessel.mainBody);
                        if (targetGroundPos != Vector3d.zero)
                        {
                            return Vector3d.Distance(targetGroundPos, vessel.ProjectPositionOntoSurface());
                        }
                    }
                    return -1d;
                case "RELATIVEINCLINATION":
                    // MechJeb's targetables don't have orbits.
                    if (target != null && targetOrbit != null)
                    {
                        return targetOrbit.referenceBody != vessel.orbit.referenceBody ?
                            -1d :
                            Math.Abs(Vector3d.Angle(vessel.GetOrbit().SwappedOrbitNormal(), targetOrbit.SwappedOrbitNormal()));
                    }
                    return double.NaN;
                case "TARGETORBITBODY":
                    if (target != null && targetOrbit != null)
                        return targetOrbit.referenceBody.name;
                    return string.Empty;
                case "TARGETEXISTS":
                    if (target == null)
                        return -1d;
                    if (target is Vessel)
                        return 1d;
                    return 0d;
                case "TARGETISDOCKINGPORT":
                    if (target == null)
                        return -1d;
                    if (target is ModuleDockingNode)
                        return 1d;
                    return 0d;
                case "TARGETISVESSELORPORT":
                    if (target == null)
                        return -1d;
                    if (target is ModuleDockingNode || target is Vessel)
                        return 1d;
                    return 0d;
                case "TARGETISCELESTIAL":
                    if (target == null)
                        return -1d;
                    if (target is CelestialBody)
                        return 1d;
                    return 0d;
                case "TARGETSITUATION":
                    if (target is Vessel)
                        return SituationString(target.GetVessel().situation);
                    return string.Empty;
                case "TARGETALTITUDE":
                    if (target == null)
                    {
                        return -1d;
                    }
                    if (target is CelestialBody)
                    {
                        if (targetBody == vessel.mainBody || targetBody == Planetarium.fetch.Sun)
                        {
                            return 0d;
                        }
                        else
                        {
                            return targetBody.referenceBody.GetAltitude(targetBody.position);
                        }
                    }
                    if (target is Vessel || target is ModuleDockingNode)
                    {
                        return target.GetVessel().mainBody.GetAltitude(target.GetVessel().CoM);
                    }
                    else
                    {
                        return vessel.mainBody.GetAltitude(target.GetTransform().position);
                    }
                // MOARdV: I don't think these are needed - I don't remember why we needed targetOrbit
                //if (targetOrbit != null)
                //{
                //    return targetOrbit.altitude;
                //}
                //return -1d;
                case "TARGETSEMIMAJORAXIS":
                    if (target == null)
                        return double.NaN;
                    if (targetOrbit != null)
                        return targetOrbit.semiMajorAxis;
                    return double.NaN;
                case "TIMETOANWITHTARGETSECS":
                    if (target == null || targetOrbit == null)
                        return double.NaN;
                    return vessel.GetOrbit().TimeOfAscendingNode(targetOrbit, Planetarium.GetUniversalTime()) - Planetarium.GetUniversalTime();
                case "TIMETODNWITHTARGETSECS":
                    if (target == null || targetOrbit == null)
                        return double.NaN;
                    return vessel.GetOrbit().TimeOfDescendingNode(targetOrbit, Planetarium.GetUniversalTime()) - Planetarium.GetUniversalTime();
                case "TARGETCLOSESTAPPROACHTIME":
                    if (target == null || targetOrbit == null || orbitSensibility == false)
                    {
                        return double.NaN;
                    }
                    else
                    {
                        double approachTime, approachDistance;
                        approachDistance = JUtil.GetClosestApproach(vessel.GetOrbit(), target, out approachTime);
                        return approachTime - Planetarium.GetUniversalTime();
                    }
                case "TARGETCLOSESTAPPROACHDISTANCE":
                    if (target == null || targetOrbit == null || orbitSensibility == false)
                    {
                        return double.NaN;
                    }
                    else
                    {
                        double approachTime;
                        return JUtil.GetClosestApproach(vessel.GetOrbit(), target, out approachTime);
                    }

                // Space Objects (asteroid) specifics
                case "TARGETSIGNALSTRENGTH":
                    // MOARdV:
                    // Based on observation, it appears the discovery
                    // level bitfield is basically unused - either the
                    // craft is Owned (-1) or Unowned (29 - which is the
                    // OR of all the bits).  However, maybe career mode uses
                    // the bits, so I will make a guess on what knowledge is
                    // appropriate here.
                    if (targetVessel != null && targetVessel.DiscoveryInfo.Level != DiscoveryLevels.Owned && targetVessel.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.Presence))
                    {
                        return targetVessel.DiscoveryInfo.GetSignalStrength(targetVessel.DiscoveryInfo.lastObservedTime);
                    }
                    else
                    {
                        return -1.0;
                    }

                case "TARGETSIGNALSTRENGTHCAPTION":
                    if (targetVessel != null && targetVessel.DiscoveryInfo.Level != DiscoveryLevels.Owned && targetVessel.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.Presence))
                    {
                        return DiscoveryInfo.GetSignalStrengthCaption(targetVessel.DiscoveryInfo.GetSignalStrength(targetVessel.DiscoveryInfo.lastObservedTime));
                    }
                    else
                    {
                        return "";
                    }

                case "TARGETLASTOBSERVEDTIMEUT":
                    if (targetVessel != null && targetVessel.DiscoveryInfo.Level != DiscoveryLevels.Owned && targetVessel.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.Presence))
                    {
                        return targetVessel.DiscoveryInfo.lastObservedTime;
                    }
                    else
                    {
                        return -1.0;
                    }

                case "TARGETLASTOBSERVEDTIMESECS":
                    if (targetVessel != null && targetVessel.DiscoveryInfo.Level != DiscoveryLevels.Owned && targetVessel.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.Presence))
                    {
                        return Math.Max(Planetarium.GetUniversalTime() - targetVessel.DiscoveryInfo.lastObservedTime, 0.0);
                    }
                    else
                    {
                        return -1.0;
                    }

                case "TARGETSIZECLASS":
                    if (targetVessel != null && targetVessel.DiscoveryInfo.Level != DiscoveryLevels.Owned && targetVessel.DiscoveryInfo.HaveKnowledgeAbout(DiscoveryLevels.Presence))
                    {
                        return targetVessel.DiscoveryInfo.objectSize;
                    }
                    else
                    {
                        return "";
                    }

                // Ok, what are X, Y and Z here anyway?
                case "TARGETDISTANCEX":    //distance to target along the yaw axis (j and l rcs keys)
                    return Vector3d.Dot(targetSeparation, vessel.GetTransform().right);
                case "TARGETDISTANCEY":   //distance to target along the pitch axis (i and k rcs keys)
                    return Vector3d.Dot(targetSeparation, vessel.GetTransform().forward);
                case "TARGETDISTANCEZ":  //closure distance from target - (h and n rcs keys)
                    return -Vector3d.Dot(targetSeparation, vessel.GetTransform().up);

                case "TARGETDISTANCESCALEDX":    //scaled and clamped version of TARGETDISTANCEX.  Returns a number between 100 and -100, with precision increasing as distance decreases.
                    double scaledX = Vector3d.Dot(targetSeparation, vessel.GetTransform().right);
                    double zdist = -Vector3d.Dot(targetSeparation, vessel.GetTransform().up);
                    if (zdist < .1)
                        scaledX = scaledX / (0.1 * Math.Sign(zdist));
                    else
                        scaledX = ((scaledX + zdist) / (zdist + zdist)) * (100) - 50;
                    if (scaledX > 100) scaledX = 100;
                    if (scaledX < -100) scaledX = -100;
                    return scaledX;


                case "TARGETDISTANCESCALEDY":  //scaled and clamped version of TARGETDISTANCEY.  These two numbers will control the position needles on a docking port alignment gauge.
                    double scaledY = Vector3d.Dot(targetSeparation, vessel.GetTransform().forward);
                    double zdist2 = -Vector3d.Dot(targetSeparation, vessel.GetTransform().up);
                    if (zdist2 < .1)
                        scaledY = scaledY / (0.1 * Math.Sign(zdist2));
                    else
                        scaledY = ((scaledY + zdist2) / (zdist2 + zdist2)) * (100) - 50;
                    if (scaledY > 100) scaledY = 100;
                    if (scaledY < -100) scaledY = -100;
                    return scaledY;

                // TODO: I probably should return something else for vessels. But not sure what exactly right now.
                case "TARGETANGLEX":
                    if (target != null)
                    {
                        if (targetDockingNode != null)
                            return JUtil.NormalAngle(-targetDockingNode.GetTransform().forward, FlightGlobals.ActiveVessel.ReferenceTransform.up, FlightGlobals.ActiveVessel.ReferenceTransform.forward);
                        if (target is Vessel)
                            return JUtil.NormalAngle(-target.GetFwdVector(), forward, up);
                        return 0d;
                    }
                    return 0d;
                case "TARGETANGLEY":
                    if (target != null)
                    {
                        if (targetDockingNode != null)
                            return JUtil.NormalAngle(-targetDockingNode.GetTransform().forward, FlightGlobals.ActiveVessel.ReferenceTransform.up, -FlightGlobals.ActiveVessel.ReferenceTransform.right);
                        if (target is Vessel)
                        {
                            JUtil.NormalAngle(-target.GetFwdVector(), forward, -right);
                        }
                        return 0d;
                    }
                    return 0d;
                case "TARGETANGLEZ":
                    if (target != null)
                    {
                        if (targetDockingNode != null)
                            return (360 - (JUtil.NormalAngle(-targetDockingNode.GetTransform().up, FlightGlobals.ActiveVessel.ReferenceTransform.forward, FlightGlobals.ActiveVessel.ReferenceTransform.up))) % 360;
                        if (target is Vessel)
                        {
                            return JUtil.NormalAngle(target.GetTransform().up, up, -forward);
                        }
                        return 0d;
                    }
                    return 0d;
                case "TARGETANGLEDEV":
                    if (target != null)
                    {
                        return Vector3d.Angle(vessel.ReferenceTransform.up, FlightGlobals.fetch.vesselTargetDirection);
                    }
                    return 180d;

                case "TARGETAPOAPSIS":
                    if (target != null && targetOrbitSensibility)
                        return targetOrbit.ApA;
                    return double.NaN;
                case "TARGETPERIAPSIS":
                    if (target != null && targetOrbitSensibility)
                        return targetOrbit.PeA;
                    return double.NaN;
                case "TARGETINCLINATION":
                    if (target != null && targetOrbitSensibility)
                        return targetOrbit.inclination;
                    return double.NaN;
                case "TARGETECCENTRICITY":
                    if (target != null && targetOrbitSensibility)
                        return targetOrbit.eccentricity;
                    return double.NaN;
                case "TARGETORBITALVEL":
                    if (target != null && targetOrbitSensibility)
                        return targetOrbit.orbitalSpeed;
                    return double.NaN;
                case "TARGETTIMETOAPSECS":
                    if (target != null && targetOrbitSensibility)
                        return targetOrbit.timeToAp;
                    return double.NaN;
                case "TARGETORBPERIODSECS":
                    if (target != null && targetOrbit != null && targetOrbitSensibility)
                        return targetOrbit.period;
                    return double.NaN;
                case "TARGETTIMETOPESECS":
                    if (target != null && targetOrbitSensibility)
                        return targetOrbit.eccentricity < 1 ?
                            targetOrbit.timeToPe :
                            -targetOrbit.meanAnomaly / (2 * Math.PI / targetOrbit.period);
                    return double.NaN;

                // Protractor-type values (phase angle, ejection angle)
                case "TARGETBODYPHASEANGLE":
                    // targetOrbit is always null if targetOrbitSensibility is false,
                    // so no need to test if the orbit makes sense.
                    protractor.Update(vessel, altitudeASL, targetOrbit);
                    return protractor.PhaseAngle;
                case "TARGETBODYPHASEANGLESECS":
                    protractor.Update(vessel, altitudeASL, targetOrbit);
                    return protractor.TimeToPhaseAngle;
                case "TARGETBODYEJECTIONANGLE":
                    protractor.Update(vessel, altitudeASL, targetOrbit);
                    return protractor.EjectionAngle;
                case "TARGETBODYEJECTIONANGLESECS":
                    protractor.Update(vessel, altitudeASL, targetOrbit);
                    return protractor.TimeToEjectionAngle;
                case "TARGETBODYCLOSESTAPPROACH":
                    if (orbitSensibility == true)
                    {
                        double approachTime;
                        return JUtil.GetClosestApproach(vessel.GetOrbit(), target, out approachTime);
                    }
                    else
                    {
                        return -1.0;
                    }
                case "TARGETBODYMOONEJECTIONANGLE":
                    protractor.Update(vessel, altitudeASL, targetOrbit);
                    return protractor.MoonEjectionAngle;
                case "TARGETBODYEJECTIONALTITUDE":
                    protractor.Update(vessel, altitudeASL, targetOrbit);
                    return protractor.EjectionAltitude;
                case "TARGETBODYDELTAV":
                    protractor.Update(vessel, altitudeASL, targetOrbit);
                    return protractor.TargetBodyDeltaV;

                case "PREDICTEDLANDINGALTITUDE":
                    return LandingAltitude();
                case "PREDICTEDLANDINGLATITUDE":
                    return LandingLatitude();
                case "PREDICTEDLANDINGLONGITUDE":
                    return LandingLongitude();
                case "PREDICTEDLANDINGERROR":
                    return LandingError();

                // FLight control status
                case "THROTTLE":
                    return vessel.ctrlState.mainThrottle;
                case "STICKPITCH":
                    return vessel.ctrlState.pitch;
                case "STICKROLL":
                    return vessel.ctrlState.roll;
                case "STICKYAW":
                    return vessel.ctrlState.yaw;
                case "STICKPITCHTRIM":
                    return vessel.ctrlState.pitchTrim;
                case "STICKROLLTRIM":
                    return vessel.ctrlState.rollTrim;
                case "STICKYAWTRIM":
                    return vessel.ctrlState.yawTrim;
                case "STICKRCSX":
                    return vessel.ctrlState.X;
                case "STICKRCSY":
                    return vessel.ctrlState.Y;
                case "STICKRCSZ":
                    return vessel.ctrlState.Z;
                case "PRECISIONCONTROL":
                    return (FlightInputHandler.fetch.precisionMode).GetHashCode();

                // Staging and other stuff
                case "STAGE":
                    return Staging.CurrentStage;
                case "STAGEREADY":
                    return (Staging.separate_ready && InputLockManager.IsUnlocked(ControlTypes.STAGING)).GetHashCode();
                case "SITUATION":
                    return SituationString(vessel.situation);
                case "RANDOM":
                    cacheable = false;
                    return UnityEngine.Random.value;
                case "PODTEMPERATURE":
                    return (part != null) ? (part.temperature + KelvinToCelsius) : 0.0;
                case "PODTEMPERATUREKELVIN":
                    return (part != null) ? (part.temperature) : 0.0;
                case "PODSKINTEMPERATURE":
                    return (part != null) ? (part.skinTemperature + KelvinToCelsius) : 0.0;
                case "PODSKINTEMPERATUREKELVIN":
                    return (part != null) ? (part.skinTemperature) : 0.0;
                case "PODMAXTEMPERATURE":
                    return (part != null) ? (part.maxTemp + KelvinToCelsius) : 0.0;
                case "PODMAXTEMPERATUREKELVIN":
                    return (part != null) ? (part.maxTemp) : 0.0;
                case "PODNETFLUX":
                    return (part != null) ? (part.thermalConductionFlux + part.thermalConvectionFlux + part.thermalInternalFlux + part.thermalRadiationFlux) : 0.0;
                case "EXTERNALTEMPERATURE":
                    return vessel.externalTemperature + KelvinToCelsius;
                case "EXTERNALTEMPERATUREKELVIN":
                    return vessel.externalTemperature;
                case "HEATSHIELDTEMPERATURE":
                    return (double)heatShieldTemperature + KelvinToCelsius;
                case "HEATSHIELDTEMPERATUREKELVIN":
                    return heatShieldTemperature;
                case "HEATSHIELDTEMPERATUREFLUX":
                    return heatShieldFlux;
                case "SLOPEANGLE":
                    return slopeAngle;
                case "SPEEDDISPLAYMODE":
                    switch (FlightUIController.speedDisplayMode)
                    {
                        case FlightUIController.SpeedDisplayModes.Orbit:
                            return 1d;
                        case FlightUIController.SpeedDisplayModes.Surface:
                            return 0d;
                        case FlightUIController.SpeedDisplayModes.Target:
                            return -1d;
                    }
                    return double.NaN;
                case "ISONKERBINTIME":
                    return GameSettings.KERBIN_TIME.GetHashCode();
                case "ISDOCKINGPORTREFERENCE":
                    ModuleDockingNode thatPort = null;
                    foreach (PartModule thatModule in vessel.GetReferenceTransformPart().Modules)
                    {
                        thatPort = thatModule as ModuleDockingNode;
                        if (thatPort != null)
                            break;
                    }
                    if (thatPort != null)
                        return 1d;
                    return 0d;
                case "ISCLAWREFERENCE":
                    ModuleGrappleNode thatClaw = null;
                    foreach (PartModule thatModule in vessel.GetReferenceTransformPart().Modules)
                    {
                        thatClaw = thatModule as ModuleGrappleNode;
                        if (thatClaw != null)
                            break;
                    }
                    if (thatClaw != null)
                        return 1d;
                    return 0d;
                case "ISREMOTEREFERENCE":
                    ModuleCommand thatPod = null;
                    foreach (PartModule thatModule in vessel.GetReferenceTransformPart().Modules)
                    {
                        thatPod = thatModule as ModuleCommand;
                        if (thatPod != null)
                            break;
                    }
                    if (thatPod == null)
                        return 1d;
                    return 0d;
                case "FLIGHTUIMODE":
                    switch (FlightUIModeController.Instance.Mode)
                    {
                        case FlightUIMode.DOCKING:
                            return 1d;
                        case FlightUIMode.STAGING:
                            return -1d;
                        case FlightUIMode.ORBITAL:
                            return 0d;
                    }
                    return double.NaN;

                // Meta.
                case "RPMVERSION":
                    return FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).FileVersion;
                // That would return only the "AssemblyVersion" version which in our case does not change anymore.
                // We use "AsssemblyFileVersion" for actual version numbers now to facilitate hardlinking.
                // return Assembly.GetExecutingAssembly().GetName().Version.ToString();

                case "MECHJEBAVAILABLE":
                    return MechJebAvailable().GetHashCode();

                // Compound variables which exist to stave off the need to parse logical and arithmetic expressions. :)
                case "GEARALARM":
                    // Returns 1 if vertical speed is negative, gear is not extended, and radar altitude is less than 50m.
                    return (speedVerticalRounded < 0 && !vessel.ActionGroups.groups[RPMVesselComputer.gearGroupNumber] && altitudeBottom < 100).GetHashCode();
                case "GROUNDPROXIMITYALARM":
                    // Returns 1 if, at maximum acceleration, in the time remaining until ground impact, it is impossible to get a vertical speed higher than -10m/s.
                    return (SpeedAtImpact(totalMaximumThrust) < -10d).GetHashCode();
                case "TUMBLEALARM":
                    return (speedVerticalRounded < 0 && altitudeBottom < 100 && speedHorizontal > 5).GetHashCode();
                case "SLOPEALARM":
                    return (speedVerticalRounded < 0.0 && altitudeBottom < 100.0 && slopeAngle > 15.0f).GetHashCode();
                case "DOCKINGANGLEALARM":
                    return (targetDockingNode != null && targetDistance < 10 && approachSpeed > 0 &&
                    (Math.Abs(JUtil.NormalAngle(-targetDockingNode.GetFwdVector(), forward, up)) > 1.5 ||
                    Math.Abs(JUtil.NormalAngle(-targetDockingNode.GetFwdVector(), forward, -right)) > 1.5)).GetHashCode();
                case "DOCKINGSPEEDALARM":
                    return (targetDockingNode != null && approachSpeed > 2.5 && targetDistance < 15).GetHashCode();
                case "ALTITUDEALARM":
                    return (speedVerticalRounded < 0 && altitudeBottom < 150).GetHashCode();
                case "PODTEMPERATUREALARM":
                    if (part != null)
                    {
                        double tempRatio = part.temperature / part.maxTemp;
                        if (tempRatio > 0.85d)
                        {
                            return 1d;
                        }
                        else if (tempRatio > 0.75d)
                        {
                            return 0d;
                        }
                    }
                    return -1d;
                // Well, it's not a compound but it's an alarm...
                case "ENGINEOVERHEATALARM":
                    return anyEnginesOverheating.GetHashCode();
                case "ENGINEFLAMEOUTALARM":
                    return anyEnginesFlameout.GetHashCode();
                case "IMPACTALARM":
                    return (part != null && vessel.srfSpeed > part.crashTolerance).GetHashCode();

                // SCIENCE!!
                case "SCIENCEDATA":
                    return totalDataAmount;
                case "SCIENCECOUNT":
                    return totalExperimentCount;
                case "BIOMENAME":
                    return vessel.CurrentBiome();
                case "BIOMEID":
                    return ScienceUtil.GetExperimentBiome(vessel.mainBody, vessel.latitude, vessel.longitude);

                // Some of the new goodies in 0.24.
                case "REPUTATION":
                    return Reputation.Instance != null ? (double)Reputation.CurrentRep : 0;
                case "FUNDS":
                    return Funding.Instance != null ? Funding.Instance.Funds : 0;

                // Action group flags. To properly format those, use this format:
                // {0:on;0;OFF}
                case "GEAR":
                    return vessel.ActionGroups.groups[RPMVesselComputer.gearGroupNumber].GetHashCode();
                case "BRAKES":
                    return vessel.ActionGroups.groups[RPMVesselComputer.brakeGroupNumber].GetHashCode();
                case "SAS":
                    return vessel.ActionGroups.groups[RPMVesselComputer.sasGroupNumber].GetHashCode();
                case "LIGHTS":
                    return vessel.ActionGroups.groups[RPMVesselComputer.lightGroupNumber].GetHashCode();
                case "RCS":
                    return vessel.ActionGroups.groups[RPMVesselComputer.rcsGroupNumber].GetHashCode();

                // 0.90 SAS mode fields:
                case "SASMODESTABILITY":
                    return (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.StabilityAssist) ? 1.0 : 0.0;
                case "SASMODEPROGRADE":
                    return (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.Prograde) ? 1.0 :
                        (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.Retrograde) ? -1.0 : 0.0;
                case "SASMODENORMAL":
                    return (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.Normal) ? 1.0 :
                        (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.Antinormal) ? -1.0 : 0.0;
                case "SASMODERADIAL":
                    return (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.RadialOut) ? 1.0 :
                        (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.RadialIn) ? -1.0 : 0.0;
                case "SASMODETARGET":
                    return (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.Target) ? 1.0 :
                        (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.AntiTarget) ? -1.0 : 0.0;
                case "SASMODEMANEUVER":
                    return (vessel.Autopilot.Mode == VesselAutopilot.AutopilotMode.Maneuver) ? 1.0 : 0.0;

                // Database information about planetary bodies.
                case "ORBITBODYATMOSPHERE":
                    return vessel.orbit.referenceBody.atmosphere ? 1d : -1d;
                case "TARGETBODYATMOSPHERE":
                    if (targetBody != null)
                        return targetBody.atmosphere ? 1d : -1d;
                    return 0d;
                case "ORBITBODYOXYGEN":
                    return vessel.orbit.referenceBody.atmosphereContainsOxygen ? 1d : -1d;
                case "TARGETBODYOXYGEN":
                    if (targetBody != null)
                        return targetBody.atmosphereContainsOxygen ? 1d : -1d;
                    return -1d;
                case "ORBITBODYSCALEHEIGHT":
                    return vessel.orbit.referenceBody.atmosphereDepth;
                case "TARGETBODYSCALEHEIGHT":
                    if (targetBody != null)
                        return targetBody.atmosphereDepth;
                    return -1d;
                case "ORBITBODYRADIUS":
                    return vessel.orbit.referenceBody.Radius;
                case "TARGETBODYRADIUS":
                    if (targetBody != null)
                        return targetBody.Radius;
                    return -1d;
                case "ORBITBODYMASS":
                    return vessel.orbit.referenceBody.Mass;
                case "TARGETBODYMASS":
                    if (targetBody != null)
                        return targetBody.Mass;
                    return -1d;
                case "ORBITBODYROTATIONPERIOD":
                    return vessel.orbit.referenceBody.rotationPeriod;
                case "TARGETBODYROTATIONPERIOD":
                    if (targetBody != null)
                        return targetBody.rotationPeriod;
                    return -1d;
                case "ORBITBODYSOI":
                    return vessel.orbit.referenceBody.sphereOfInfluence;
                case "TARGETBODYSOI":
                    if (targetBody != null)
                        return targetBody.sphereOfInfluence;
                    return -1d;
                case "ORBITBODYGEEASL":
                    return vessel.orbit.referenceBody.GeeASL;
                case "TARGETBODYGEEASL":
                    if (targetBody != null)
                        return targetBody.GeeASL;
                    return -1d;
                case "ORBITBODYGM":
                    return vessel.orbit.referenceBody.gravParameter;
                case "TARGETBODYGM":
                    if (targetBody != null)
                        return targetBody.gravParameter;
                    return -1d;
                case "ORBITBODYATMOSPHERETOP":
                    return vessel.orbit.referenceBody.atmosphereDepth;
                case "TARGETBODYATMOSPHERETOP":
                    if (targetBody != null)
                        return targetBody.atmosphereDepth;
                    return -1d;
                case "ORBITBODYESCAPEVEL":
                    return Math.Sqrt(2 * vessel.orbit.referenceBody.gravParameter / vessel.orbit.referenceBody.Radius);
                case "TARGETBODYESCAPEVEL":
                    if (targetBody != null)
                        return Math.Sqrt(2 * targetBody.gravParameter / targetBody.Radius);
                    return -1d;
                case "ORBITBODYAREA":
                    return 4 * Math.PI * vessel.orbit.referenceBody.Radius * vessel.orbit.referenceBody.Radius;
                case "TARGETBODYAREA":
                    if (targetBody != null)
                        return 4 * Math.PI * targetBody.Radius * targetBody.Radius;
                    return -1d;
                case "ORBITBODYSYNCORBITALTITUDE":
                    double syncRadius = Math.Pow(vessel.orbit.referenceBody.gravParameter / Math.Pow(2 * Math.PI / vessel.orbit.referenceBody.rotationPeriod, 2), 1 / 3d);
                    return syncRadius > vessel.orbit.referenceBody.sphereOfInfluence ? double.NaN : syncRadius - vessel.orbit.referenceBody.Radius;
                case "TARGETBODYSYNCORBITALTITUDE":
                    if (targetBody != null)
                    {
                        double syncRadiusT = Math.Pow(targetBody.gravParameter / Math.Pow(2 * Math.PI / targetBody.rotationPeriod, 2), 1 / 3d);
                        return syncRadiusT > targetBody.sphereOfInfluence ? double.NaN : syncRadiusT - targetBody.Radius;
                    }
                    return -1d;
                case "ORBITBODYSYNCORBITVELOCITY":
                    return (2 * Math.PI / vessel.orbit.referenceBody.rotationPeriod) *
                    Math.Pow(vessel.orbit.referenceBody.gravParameter / Math.Pow(2 * Math.PI / vessel.orbit.referenceBody.rotationPeriod, 2), 1 / 3d);
                case "TARGETBODYSYNCORBITVELOCITY":
                    if (targetBody != null)
                    {
                        return (2 * Math.PI / targetBody.rotationPeriod) *
                        Math.Pow(targetBody.gravParameter / Math.Pow(2 * Math.PI / targetBody.rotationPeriod, 2), 1 / 3d);
                    }
                    return -1d;
                case "ORBITBODYSYNCORBITCIRCUMFERENCE":
                    return 2 * Math.PI * Math.Pow(vessel.orbit.referenceBody.gravParameter / Math.Pow(2 * Math.PI / vessel.orbit.referenceBody.rotationPeriod, 2), 1 / 3d);
                case "TARGETBODYSYNCORBICIRCUMFERENCE":
                    if (targetBody != null)
                    {
                        return 2 * Math.PI * Math.Pow(targetBody.gravParameter / Math.Pow(2 * Math.PI / targetBody.rotationPeriod, 2), 1 / 3d);
                    }
                    return -1d;
            }
            return null;
        }
        #endregion
    }
}
