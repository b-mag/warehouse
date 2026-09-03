"use client";

/**
 * A throttled wall-clock hook for the scene (task 37.2).
 *
 * Reading `Date.now()` during render is impure; instead this hook advances a clock inside
 * the R3F frame loop and publishes the current time roughly once per second. It is used to
 * evaluate starship loading windows ([start, end]) without re-rendering every frame. This
 * is a presentation clock only — it drives no business rules.
 */

import { useFrame } from "@react-three/fiber";
import { useRef } from "react";

/** Invoke `onTick(nowMs)` about once per second, driven by the render loop. */
export function useSimClock(onTick: (nowMs: number) => void): void {
  const accum = useRef(0);
  useFrame((_, delta) => {
    accum.current += delta;
    if (accum.current >= 1) {
      accum.current = 0;
      onTick(Date.now());
    }
  });
}
