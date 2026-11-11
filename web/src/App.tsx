import { useQuery } from "@tanstack/react-query";
import { useTranslation } from "react-i18next";
import { Navigate, NavLink, Route, Routes, useLocation } from "react-router";

import { api, call } from "./api.ts";
import { ErrorMessage, InsecureBanner } from "./components/ui.tsx";
import { Account } from "./pages/Account.tsx";
import { Devices } from "./pages/Devices.tsx";
import { Setup } from "./pages/Setup.tsx";
import { SignIn } from "./pages/SignIn.tsx";
import { useSession } from "./session.tsx";

/**
 * Decides what the user sees:
 * - a server that is not set up shows the wizard,
 * - a signed-out user shows the sign-in page,
 * - otherwise the app.
 * The address is left untouched while signing in, so a link such as
 * /pair?code=… opens where it pointed once the user is signed in.
 */
export function App() {
  const { t } = useTranslation();
  const { user } = useSession();
  const setup = useQuery({
    queryKey: ["setup-state"],
    queryFn: () => call(api.GET("/api/v1/setup/state")),
  });

  if (setup.isPending) {
    return (
      <main className="card">
        <p>{t("common.loading")}</p>
      </main>
    );
  }

  if (setup.isError) {
    return (
      <main className="card">
        <ErrorMessage error={setup.error} />
        <div className="actions">
          <button type="button" onClick={() => void setup.refetch()}>
            {t("common.retry")}
          </button>
        </div>
      </main>
    );
  }

  if (!setup.data.completed) {
    // Once an admin exists the remaining steps need that admin signed in.
    return setup.data.adminExists && !user ? <SignIn /> : <Setup state={setup.data} />;
  }

  return user ? <Shell /> : <SignIn />;
}

function Shell() {
  const { t } = useTranslation();
  const { user, signOut } = useSession();
  const location = useLocation();

  return (
    <div className="shell">
      <InsecureBanner />
      <header className="topbar">
        <span className="brand">Atrium</span>
        <nav aria-label={t("nav.main")}>
          <NavLink to="/devices">{t("nav.devices")}</NavLink>
          <NavLink to="/account">{t("nav.account")}</NavLink>
        </nav>
        <span className="who">{user?.displayName}</span>
        <button type="button" onClick={() => void signOut()}>
          {t("common.signOut")}
        </button>
      </header>
      <main className="content">
        <Routes>
          <Route path="/devices" element={<Devices />} />
          {/* The address a device's QR code points at: opens the approval with the code filled in. */}
          <Route path="/pair" element={<Navigate to={`/devices${location.search}`} replace />} />
          <Route path="/account" element={<Account />} />
          <Route path="*" element={<Navigate to="/devices" replace />} />
        </Routes>
      </main>
    </div>
  );
}
