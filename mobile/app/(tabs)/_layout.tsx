import { Redirect } from "expo-router";
import { Tabs } from "expo-router/js-tabs";
import { useTranslation } from "react-i18next";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { Icon, type IconName } from "../../src/components/Icon.tsx";
import { useNotifications } from "../../src/notifications.ts";
import { useServers } from "../../src/servers.tsx";
import { SyncProvider } from "../../src/sync/SyncProvider.tsx";
import { UploadsPanel } from "../../src/uploads/UploadsPanel.tsx";
import { UploadsProvider } from "../../src/uploads/UploadsProvider.tsx";
import { space, useTheme } from "../../src/theme.ts";

/**
 * The signed-in app: Photos, Files and Account along the bottom edge
 * (docs/UX.md, Mobile, Navigation). It exists only while a server is in
 * front, and starts over when another server comes to the front, so no
 * screen of one server stays open over another.
 */
export default function TabsLayout() {
  const { active } = useServers();
  if (!active) {
    return <Redirect href="/welcome" />;
  }

  // Uploads belong to the server in front, and start over with another.
  return (
    <UploadsProvider key={active.id}>
      <SyncProvider>
        <SignedInTabs />
      </SyncProvider>
    </UploadsProvider>
  );
}

function SignedInTabs() {
  const { t } = useTranslation();
  const { colors } = useTheme();
  const insets = useSafeAreaInsets();

  const unread = Number(useNotifications().data?.unread ?? 0);

  const icon =
    (name: IconName) =>
    ({ color }: { readonly color: unknown }) => <Icon name={name} {...(typeof color === "string" ? { color } : {})} />;

  return (
    <>
      <Tabs
        screenOptions={{
          headerShown: false,
          tabBarActiveTintColor: colors.accent,
          tabBarInactiveTintColor: colors.inkFaint,
          // Room for the icon and a label that grows with the system's text size.
          tabBarStyle: {
            backgroundColor: colors.surface,
            borderTopColor: colors.line,
            height: 64 + insets.bottom,
            paddingTop: 6,
            paddingBottom: insets.bottom + 6,
          },
          tabBarLabelStyle: { fontSize: 12, fontWeight: "600" },
          sceneStyle: { backgroundColor: colors.paper },
        }}
      >
        <Tabs.Screen name="photos" options={{ title: t("nav.photos"), tabBarIcon: icon("image") }} />
        <Tabs.Screen name="files" options={{ title: t("nav.files"), tabBarIcon: icon("folder") }} />
        <Tabs.Screen
          name="account"
          options={{
            title: t("nav.account"),
            tabBarIcon: icon("user"),
            ...(unread > 0 ? { tabBarBadge: unread > 99 ? "99+" : unread } : {}),
          }}
        />
      </Tabs>
      <UploadsPanel bottom={64 + insets.bottom + space.sm} />
    </>
  );
}
