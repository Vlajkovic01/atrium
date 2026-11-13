import { useEffect, useId, useRef, useState, type KeyboardEvent, type ReactNode } from "react";
import { useTranslation } from "react-i18next";
import { useLocation, useNavigate } from "react-router";

import { useSession } from "../session.tsx";
import type { IconName } from "./Icon.tsx";
import { Icon } from "./Icon.tsx";
import { Avatar } from "./ui.tsx";

interface Item {
  readonly id: string;
  readonly label: string;
  readonly icon: IconName;
  readonly onSelect: () => void;
  readonly badge?: ReactNode;
  readonly separated?: boolean;
}

/**
 * The person's own menu, opened by clicking their name (D-043): their
 * account, arranging the menu, Administration for admins, and signing
 * out. It follows the menu button pattern: arrows move, Enter chooses,
 * Escape closes and returns focus to the name.
 */
/**
 * @param adminElsewhere Administration already shows in the menu itself
 * (the admin placed it at the top), so it is never repeated here.
 */
export function UserMenu(props: { readonly adminWarnings: number; readonly adminElsewhere: boolean }) {
  const { t } = useTranslation();
  const { user, signOut } = useSession();
  const navigate = useNavigate();
  const location = useLocation();
  const id = useId();
  const root = useRef<HTMLDivElement>(null);
  const button = useRef<HTMLButtonElement>(null);
  const items = useRef<(HTMLButtonElement | null)[]>([]);
  const [open, setOpen] = useState(false);
  const offerAdmin = user?.role === "admin" && !props.adminElsewhere;

  // Close on a click elsewhere and whenever the page changes.
  useEffect(() => {
    if (!open) {
      return;
    }

    const away = (event: MouseEvent): void => {
      if (!root.current?.contains(event.target as Node)) {
        setOpen(false);
      }
    };
    document.addEventListener("mousedown", away);
    return () => document.removeEventListener("mousedown", away);
  }, [open]);
  useEffect(() => setOpen(false), [location.pathname]);
  useEffect(() => {
    if (open) {
      items.current[0]?.focus();
    }
  }, [open]);

  const go = (path: string) => () => void navigate(path);
  const entries: Item[] = [
    { id: "account", label: t("userMenu.account"), icon: "user", onSelect: go("/account") },
    { id: "menu", label: t("nav.customize"), icon: "pencil", onSelect: go("/menu") },
    ...(offerAdmin
      ? [
          {
            id: "admin",
            label: t("nav.administration"),
            icon: "shield" as IconName,
            onSelect: go("/admin"),
            badge:
              props.adminWarnings > 0 ? (
                <span className="count-badge" aria-label={t("admin.warningCount", { count: props.adminWarnings })}>
                  {props.adminWarnings}
                </span>
              ) : null,
          },
        ]
      : []),
    {
      id: "signout",
      label: t("common.signOut"),
      icon: "logout",
      onSelect: () => void signOut(),
      separated: true,
    },
  ];

  const keys = (event: KeyboardEvent): void => {
    const index = items.current.findIndex((item) => item === document.activeElement);
    const move = (to: number): void => {
      event.preventDefault();
      items.current[(to + entries.length) % entries.length]?.focus();
    };
    switch (event.key) {
      case "ArrowDown":
        move(index + 1);
        break;
      case "ArrowUp":
        move(index - 1);
        break;
      case "Home":
        move(0);
        break;
      case "End":
        move(entries.length - 1);
        break;
      case "Escape":
        event.preventDefault();
        setOpen(false);
        button.current?.focus();
        break;
      case "Tab":
        setOpen(false);
        break;
    }
  };

  return (
    <div className="sidebar-user user-menu" ref={root}>
      <button
        ref={button}
        type="button"
        className="user-menu-button"
        aria-haspopup="menu"
        aria-label={t("userMenu.open", { name: user?.displayName ?? "" })}
        aria-expanded={open}
        aria-controls={open ? `${id}-menu` : undefined}
        onClick={() => setOpen(!open)}
        onKeyDown={(event) => {
          if (event.key === "ArrowDown" || event.key === "ArrowUp") {
            event.preventDefault();
            setOpen(true);
          }
        }}
      >
        <Avatar name={user?.displayName ?? ""} />
        <span className="user-text">
          <span className="user-name">{user?.displayName}</span>
          <span className="user-role">{user ? t(`roles.${user.role}`) : ""}</span>
        </span>
        {props.adminWarnings > 0 ? <span className="user-menu-dot" aria-hidden="true" /> : null}
        <Icon name="chevronUp" size={16} />
      </button>
      {open ? (
        <div id={`${id}-menu`} className="user-menu-list" role="menu" aria-label={t("userMenu.label")} onKeyDown={keys}>
          {entries.map((entry, index) => (
            <button
              key={entry.id}
              ref={(element) => {
                items.current[index] = element;
              }}
              type="button"
              role="menuitem"
              tabIndex={-1}
              className={entry.separated ? "user-menu-item is-separated" : "user-menu-item"}
              onClick={() => {
                setOpen(false);
                entry.onSelect();
              }}
            >
              <Icon name={entry.icon} size={18} />
              <span>{entry.label}</span>
              {entry.badge}
            </button>
          ))}
        </div>
      ) : null}
    </div>
  );
}
