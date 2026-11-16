import { useQuery } from "@tanstack/react-query";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { Navigate, NavLink, Route, Routes, useLocation } from "react-router";

import type { components } from "@atrium/shared";

import { api, call } from "./api.ts";
import { AuthLayout } from "./components/AuthLayout.tsx";
import type { IconName } from "./components/Icon.tsx";
import { Icon, isIconName } from "./components/Icon.tsx";
import { Logo } from "./components/Logo.tsx";
import { Avatar, Button, ErrorMessage, InsecureBanner } from "./components/ui.tsx";
import { Account } from "./pages/Account.tsx";
import { BackgroundWork } from "./pages/BackgroundWork.tsx";
import { FileBrowser } from "./files/FileBrowser.tsx";
import { Trash } from "./files/Trash.tsx";
import { UploadsProvider } from "./files/uploads.tsx";
import { AlbumPage, Albums } from "./photos/Albums.tsx";
import { Photos } from "./photos/Photos.tsx";
import { Search } from "./photos/Search.tsx";
import { MenuSettings } from "./pages/MenuSettings.tsx";
import { ViewPage } from "./pages/ViewPage.tsx";
import { Devices } from "./pages/Devices.tsx";
import { Libraries } from "./pages/Libraries.tsx";
import { People } from "./pages/People.tsx";
import { Setup } from "./pages/Setup.tsx";
import { SignIn } from "./pages/SignIn.tsx";
import { StorageLocations } from "./pages/StorageLocations.tsx";
import { toLayout, useNavigation, useSaveNavigation } from "./navigation.ts";
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
    return <div className="splash" role="status" aria-label={t("common.loading")} />;
  }

  if (setup.isError) {
    return (
      <AuthLayout>
        <ErrorMessage error={setup.error} />
        <Button onClick={() => void setup.refetch()} icon="refresh">
          {t("common.retry")}
        </Button>
      </AuthLayout>
    );
  }

  if (!setup.data.completed) {
    // Once an admin exists the remaining steps need that admin signed in.
    return setup.data.adminExists && !user ? <SignIn /> : <Setup state={setup.data} />;
  }

  return user ? <Shell /> : <SignIn />;
}

type NavItemView = components["schemas"]["NavItemView"];

interface NavItem {
  readonly to: string;
  readonly label: string;
  readonly icon: IconName;
}

function Shell() {
  const { t } = useTranslation();
  const { user, signOut } = useSession();
  const location = useLocation();
  const isAdmin = user?.role === "admin";

  // The menu as this person arranged it (D-041): order, hidden entries,
  // own names, groups, folders shared with them and their saved views.
  const navigation = useNavigation();
  const saveNavigation = useSaveNavigation();
  const [folding, setFolding] = useState(false);
  const items = (navigation.data?.items ?? []).filter((item) => !item.hidden);
  const groups = navigation.data?.groups ?? [];
  const compact = navigation.data?.density === "compact";
  const adminOnTop = navigation.data?.adminPosition === "top";

  // Folding a group is remembered, like any other change to the menu.
  const fold = async (groupId: string): Promise<void> => {
    if (!navigation.data || folding) {
      return;
    }

    setFolding(true);
    try {
      const layout = toLayout(navigation.data);
      await saveNavigation({
        ...layout,
        groups: layout.groups.map((g) => (g.id === groupId ? { ...g, collapsed: !g.collapsed } : g)),
      });
    } catch {
      // The menu stays as it was; nothing is lost.
    } finally {
      setFolding(false);
    }
  };
  const admin: readonly NavItem[] = [
    { to: "/people", label: t("nav.people"), icon: "users" },
    { to: "/storage", label: t("nav.storage"), icon: "drive" },
    { to: "/libraries", label: t("nav.libraries"), icon: "folder" },
    { to: "/background-work", label: t("nav.backgroundWork"), icon: "history" },
  ];

  const navEntry = (item: NavItemView) => {
    const builtIn: Record<string, { to: string; icon: IconName }> = {
      files: { to: "/files", icon: "folder" },
      photos: { to: "/photos", icon: "image" },
      trash: { to: "/trash", icon: "trash" },
      devices: { to: "/devices", icon: "devices" },
      account: { to: "/account", icon: "user" },
    };
    if (item.kind === "separator") {
      return <hr key={item.key} className="nav-separator" />;
    }

    const target =
      item.kind === "library" || item.kind === "folder"
        ? { to: `/files/${item.targetId ?? ""}`, icon: (item.readOnly ? "lock" : "folder") as IconName }
        : item.kind === "view"
          ? { to: `/views/${item.targetId ?? ""}`, icon: "search" as IconName }
          : item.kind === "album"
            ? { to: `/photos/albums/${item.targetId ?? ""}`, icon: "album" as IconName }
            : builtIn[item.kind];
    if (!target) {
      return null;
    }

    const label = item.label ?? item.name ?? t(`nav.${item.kind}`);
    const extra = !builtIn[item.kind];
    return (
      <NavLink
        key={item.key}
        to={target.to}
        className={extra ? "nav-link nav-extra" : "nav-link"}
        title={compact ? label : undefined}
      >
        <Icon name={isIconName(item.icon) ? item.icon : target.icon} size={20} />
        <span>{label}</span>
      </NavLink>
    );
  };

  const administration = (
    <div className="nav-group" role="group" aria-label={t("nav.administration")}>
      <p className="nav-heading">{t("nav.administration")}</p>
      {admin.map((item) => link(item))}
    </div>
  );

  function link(item: NavItem) {
    return (
      <NavLink key={item.to} to={item.to} className="nav-link" title={compact ? item.label : undefined}>
        <Icon name={item.icon} size={20} />
        <span>{item.label}</span>
      </NavLink>
    );
  }

  return (
    <UploadsProvider>
      <div className={compact ? "app is-compact" : "app"}>
        <aside className="sidebar">
          <div className="sidebar-brand">
            <Logo size={30} />
          </div>
          <nav className="nav" aria-label={t("nav.main")}>
            {isAdmin && adminOnTop ? administration : null}
            {items.filter((item) => !item.group).map((item) => navEntry(item))}
            {groups.map((group) => {
              const members = items.filter((item) => item.group === group.id);
              return members.length > 0 ? (
                <div key={group.id} className="nav-group" role="group" aria-label={group.name}>
                  <button
                    type="button"
                    className="nav-heading nav-fold"
                    aria-expanded={!group.collapsed}
                    title={compact ? group.name : undefined}
                    onClick={() => void fold(group.id)}
                  >
                    <Icon name={group.collapsed ? "chevronRight" : "chevronDown"} size={14} />
                    <span>{group.name}</span>
                  </button>
                  {group.collapsed ? null : members.map((item) => navEntry(item))}
                </div>
              ) : null;
            })}
            {isAdmin && !adminOnTop ? administration : null}
          </nav>
          <div className="sidebar-user">
            <Avatar name={user?.displayName ?? ""} />
            <div className="user-text">
              <span className="user-name">{user?.displayName}</span>
              <span className="user-role">{user ? t(`roles.${user.role}`) : ""}</span>
            </div>
            <NavLink to="/menu" className="icon-button" aria-label={t("nav.customize")} title={t("nav.customize")}>
              <Icon name="pencil" size={16} />
            </NavLink>
            <button
              type="button"
              className="icon-button"
              aria-label={t("common.signOut")}
              onClick={() => void signOut()}
            >
              <Icon name="logout" size={18} />
            </button>
          </div>
        </aside>

        <div className="app-main">
          <InsecureBanner />
          <main className="content">
            <Routes>
              <Route path="/files" element={<FileBrowser />} />
              <Route path="/files/:folderId" element={<FileBrowser />} />
              <Route path="/photos" element={<Photos />} />
              <Route path="/photos/albums" element={<Albums />} />
              <Route path="/photos/albums/:albumId" element={<AlbumPage />} />
              <Route path="/search" element={<Search />} />
              <Route path="/views/:viewId" element={<ViewPage />} />
              <Route path="/menu" element={<MenuSettings />} />
              <Route path="/trash" element={<Trash />} />
              <Route path="/devices" element={<Devices />} />
              {/* The address a device's QR code points at: opens the approval with the code filled in. */}
              <Route path="/pair" element={<Navigate to={`/devices${location.search}`} replace />} />
              <Route path="/account" element={<Account />} />
              {isAdmin ? (
                <>
                  <Route path="/people" element={<People />} />
                  <Route path="/storage" element={<StorageLocations />} />
                  <Route path="/libraries" element={<Libraries />} />
                  <Route path="/background-work" element={<BackgroundWork />} />
                </>
              ) : null}
              <Route path="*" element={<Navigate to="/files" replace />} />
            </Routes>
          </main>
        </div>
      </div>
    </UploadsProvider>
  );
}
