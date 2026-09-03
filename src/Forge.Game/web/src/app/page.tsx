import { Dashboard } from "@/components/Dashboard";
import { ForgeProvider } from "@/lib/ForgeProvider";

/**
 * The Forge web operations client home page (task 37).
 *
 * Wraps the operations dashboard in the ForgeProvider, which owns the SignalR connection
 * and authoritative render-state store. The page renders only authoritative engine state
 * and issues operator commands through the Api — it computes no business rules
 * (Req 24.9, 24.10, 2.4).
 */
export default function Home() {
  return (
    <ForgeProvider>
      <Dashboard />
    </ForgeProvider>
  );
}
