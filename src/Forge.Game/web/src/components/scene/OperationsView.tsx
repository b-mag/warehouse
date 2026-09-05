"use client";

/**
 * The pseudo-3D top-down / slight-isometric operations view (task 37.2; Req 24.2, 24.3).
 *
 * Motion (agents / ships / train) comes from motionStore via useFrame — not React props —
 * so PositionsUpdate does not remount the scene tree.
 */

import { OrbitControls } from "@react-three/drei";
import { Canvas, useFrame, useThree } from "@react-three/fiber";
import {
  memo,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
  useSyncExternalStore,
} from "react";
import * as THREE from "three";

import type { SimulationSnapshotDto } from "@/lib/contracts";
import {
  ZONE_SIZE,
  groupLotsByZone,
  layoutZones,
} from "@/lib/layout";
import {
  getMotion,
  getMotionIdentityVersion,
  subscribeMotionIdentity,
} from "@/lib/motionStore";

import { Agents } from "./Agents";
import { IdleBay } from "./IdleBay";
import { Lots } from "./Lots";
import { ReceivingTrain } from "./ReceivingTrain";
import { Starships } from "./Starships";
import { WarehouseFloor } from "./WarehouseFloor";
import { Zones } from "./Zones";

interface OperationsViewProps {
  snapshot: SimulationSnapshotDto;
}

function lightingForHour(hour: number): {
  ambient: number;
  dir: number;
  hemi: number;
  bg: string;
  sky: string;
  ground: string;
} {
  if (hour >= 7 && hour < 18) {
    return {
      ambient: 0.85,
      dir: 1.15,
      hemi: 0.45,
      bg: "#0b0f14",
      sky: "#9fb8d6",
      ground: "#20252e",
    };
  }
  if (hour >= 5 && hour < 7) {
    return {
      ambient: 0.45,
      dir: 0.7,
      hemi: 0.35,
      bg: "#121018",
      sky: "#c48a6a",
      ground: "#1a1520",
    };
  }
  if (hour >= 18 && hour < 20) {
    return {
      ambient: 0.4,
      dir: 0.55,
      hemi: 0.3,
      bg: "#100e16",
      sky: "#a06050",
      ground: "#18141c",
    };
  }
  return {
    ambient: 0.22,
    dir: 0.25,
    hemi: 0.18,
    bg: "#05070c",
    sky: "#1a2230",
    ground: "#0a0c10",
  };
}

function DayNightLights() {
  const ambientRef = useRef<THREE.AmbientLight>(null);
  const dirRef = useRef<THREE.DirectionalLight>(null);
  const hemiRef = useRef<THREE.HemisphereLight>(null);

  useFrame(({ scene }) => {
    const iso = getMotion().simulatedAt;
    let hour = 12;
    if (iso) {
      const d = new Date(iso);
      if (!Number.isNaN(d.getTime())) {
        hour = d.getUTCHours() + d.getUTCMinutes() / 60;
      }
    }
    const L = lightingForHour(hour);
    if (ambientRef.current) ambientRef.current.intensity = L.ambient;
    if (dirRef.current) dirRef.current.intensity = L.dir;
    if (hemiRef.current) {
      hemiRef.current.intensity = L.hemi;
      hemiRef.current.color.set(L.sky);
      hemiRef.current.groundColor.set(L.ground);
    }
    if (scene.background instanceof THREE.Color) {
      scene.background.set(L.bg);
    }
  });

  return (
    <>
      <color attach="background" args={["#0b0f14"]} />
      <ambientLight ref={ambientRef} intensity={0.8} />
      <directionalLight ref={dirRef} position={[30, 50, 20]} intensity={1.1} />
      <hemisphereLight ref={hemiRef} args={["#9fb8d6", "#20252e", 0.4]} />
    </>
  );
}

function WebGlContextGuards({
  onContextLost,
  onContextRestored,
}: {
  onContextLost: () => void;
  onContextRestored: () => void;
}) {
  const gl = useThree((s) => s.gl);

  useEffect(() => {
    const canvas = gl.domElement;
    const onLost = (e: Event) => {
      e.preventDefault();
      console.warn("[Forge scene] WebGL context lost — attempting restore.");
      onContextLost();
    };
    const onRestored = () => {
      console.info("[Forge scene] WebGL context restored.");
      onContextRestored();
    };
    canvas.addEventListener("webglcontextlost", onLost, false);
    canvas.addEventListener("webglcontextrestored", onRestored, false);
    return () => {
      canvas.removeEventListener("webglcontextlost", onLost, false);
      canvas.removeEventListener("webglcontextrestored", onRestored, false);
    };
  }, [gl, onContextLost, onContextRestored]);

  return null;
}

/** Bumps React when inbound/transit queue lengths change so zone lots hide correctly. */
function useQueueVersion(): number {
  const [version, setVersion] = useState(0);
  const lastKey = useRef("");
  useFrame(() => {
    const m = getMotion();
    const key = `${m.inboundQueueLotIds.length}:${m.inTransitLotIds.length}:${m.train?.lotIds.length ?? 0}:${m.train?.phase ?? ""}`;
    if (key !== lastKey.current) {
      lastKey.current = key;
      setVersion((v) => v + 1);
    }
  });
  return version;
}

function SceneContents({ snapshot }: { snapshot: SimulationSnapshotDto }) {
  const { zones, lots } = snapshot;
  const identityVersion = useSyncExternalStore(
    subscribeMotionIdentity,
    getMotionIdentityVersion,
    getMotionIdentityVersion,
  );
  const queueVersion = useQueueVersion();

  const visibleLots = useMemo(() => {
    void identityVersion;
    void queueVersion;
    const m = getMotion();
    const hidden = new Set([
      ...(m.inboundQueueLotIds ?? []),
      ...(m.inTransitLotIds ?? []),
      ...(m.train?.lotIds ?? []),
    ]);
    return lots.filter((l) => !hidden.has(l.id));
  }, [lots, identityVersion, queueVersion]);

  const placements = useMemo(() => layoutZones(zones), [zones]);
  const lotsByZone = useMemo(() => groupLotsByZone(visibleLots), [visibleLots]);

  const edgeZ = useMemo(() => {
    let maxZ = 0;
    for (const p of placements) {
      maxZ = Math.max(maxZ, p.centerZ);
    }
    return maxZ + ZONE_SIZE * 1.5;
  }, [placements]);

  return (
    <>
      <DayNightLights />
      <OrbitControls
        makeDefault
        enablePan
        enableZoom
        enableRotate
        target={[0, 0, 0]}
        minPolarAngle={0.15}
        maxPolarAngle={Math.PI / 2 - 0.15}
        minDistance={20}
        maxDistance={220}
      />
      <WarehouseFloor
        placements={placements}
        openDockBays={snapshot.parameters.openDockBays}
      />
      <IdleBay />
      <Zones placements={placements} lotsByZone={lotsByZone} />
      <Lots placements={placements} lotsByZone={lotsByZone} />
      <ReceivingTrain simSpeed={snapshot.parameters.simSpeed} />
      <Agents simSpeed={snapshot.parameters.simSpeed} />
      <Starships
        edgeZ={edgeZ}
        openDockBays={snapshot.parameters.openDockBays}
        simSpeed={snapshot.parameters.simSpeed}
      />
    </>
  );
}

function SceneCanvas({
  snapshot,
  onContextLost,
  onContextRestored,
}: OperationsViewProps & {
  onContextLost: () => void;
  onContextRestored: () => void;
}) {
  return (
    <Canvas
      dpr={[1, 1.5]}
      className="h-full w-full"
      camera={{ position: [40, 44, 46], fov: 35, near: 0.1, far: 2000 }}
      gl={{ antialias: false, powerPreference: "default", failIfMajorPerformanceCaveat: false }}
    >
      <WebGlContextGuards
        onContextLost={onContextLost}
        onContextRestored={onContextRestored}
      />
      <SceneContents snapshot={snapshot} />
    </Canvas>
  );
}

function OperationsViewImpl({ snapshot }: OperationsViewProps) {
  const [canvasKey, setCanvasKey] = useState(0);
  const restoreTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  useEffect(() => {
    return () => {
      if (restoreTimer.current) {
        clearTimeout(restoreTimer.current);
        restoreTimer.current = null;
      }
    };
  }, []);

  const handleContextLost = useCallback(() => {
    if (restoreTimer.current) {
      clearTimeout(restoreTimer.current);
    }
    restoreTimer.current = setTimeout(() => {
      console.warn("[Forge scene] context not restored — remounting canvas.");
      setCanvasKey((k) => k + 1);
      restoreTimer.current = null;
    }, 750);
  }, []);

  const handleContextRestored = useCallback(() => {
    if (restoreTimer.current) {
      clearTimeout(restoreTimer.current);
      restoreTimer.current = null;
    }
  }, []);

  return (
    <SceneCanvas
      key={canvasKey}
      snapshot={snapshot}
      onContextLost={handleContextLost}
      onContextRestored={handleContextRestored}
    />
  );
}

export const OperationsView = memo(
  OperationsViewImpl,
  (prev, next) =>
    prev.snapshot.zones === next.snapshot.zones &&
    prev.snapshot.lots === next.snapshot.lots &&
    prev.snapshot.parameters.simSpeed === next.snapshot.parameters.simSpeed &&
    prev.snapshot.parameters.openDockBays === next.snapshot.parameters.openDockBays,
);
