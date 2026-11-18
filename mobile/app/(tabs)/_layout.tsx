import { Redirect } from "expo-router";
import { BottomTabBar, Tabs } from "expo-router/js-tabs";
import { useState } from "react";
import { useTranslation } from "react-i18next";
import { View } from "react-native";
import { useSafeAreaInsets } from "react-native-safe-area-context";

import { BackupProvider } from "../../src/backup/BackupProvider.tsx";
import { Icon, type IconName } from "../../src/components/Icon.tsx";
import { InsecureStrip } from "../../src/components/InsecureStrip.tsx";
import { useNotifications } from "../../src/notifications.ts";
import { OfflineProvider } from "../../src/offline/OfflineProvider.tsx";
import { useServers } from "../../src/servers.tsx";
import { SyncProvider } from "../../src/sync/SyncProvider.tsx";
import { UPLOADS_BAR_ROOM, UploadsPanel } from "../../src/uploads/UploadsPanel.tsx";
import { UploadsProvider, useUploads } from "../../src/uploads/UploadsProvider.tsx";
import { space, useTheme } from "../../src/theme.ts";

/**
 * The signed-in app: Photos, Files and Account along the bottom edge
 * (docs/UX.md, Mobile, Navigation). It exists only while a server is in
 * front, and starts over when another server comes to the front, so no
 * screen of one server stays open over another.
 */
/**
 * Height of the tab bar above the system's own bottom edge: 6 points above
 * and below, and in each tab 5 points around a 28-point icon and a label
 * of one 16-point line. Shorter, the label was squeezed and lost the tails
 * of letters such as g and j.
 */
const TAB_BAR_HEIGHT = 68;

export default function TabsLayout() {
  const { active } = useServers();
  if (!active) {
    return <Redirect href="/welcome" />;
  }

  // Uploads belong to the server in front, and start over with another.
  return (
    <UploadsProvider key={active.id}>
      <SyncProvider>
        <OfflineProvider>
          <BackupProvider>
            <SignedInTabs />
          </BackupProvider>
        </OfflineProvider>
      </SyncProvider>
    </UploadsProvider>
  );
}

function SignedInTabs() {
  const { t } = useTranslation();
  const { active } = useServers();
  // The strip over the tabs, when shown, pushes the uploads panel up by its height.
  const [stripHeight, setStripHeight] = useState(0);
  const { colors } = useTheme();
  const insets = useSafeAreaInsets();

  const unread = Number(useNotifications().data?.unread ?? 0);
  // While uploads show above the tabs, the screens keep room for them at the bottom.
  const uploadsShown = useUploads().items.length > 0;

  const icon =
    (name: IconName) =>
    ({ color }: { readonly color: unknown }) => <Icon name={name} {...(typeof color === "string" ? { color } : {})} />;

  return (
    <>
      <Tabs
        tabBar={(props) => (
          <>
            <View onLayout={(event) => setStripHeight(event.nativeEvent.layout.height)}>
              {active ? <InsecureStrip url={active.url} /> : null}
            </View>
            <BottomTabBar {...props} />
          </>
        )}
        screenOptions={{
          headerShown: false,
          tabBarActiveTintColor: colors.accent,
          tabBarInactiveTintColor: colors.inkFaint,
          // Room for the icon and a label that grows with the system's text size.
          tabBarStyle: {
            backgroundColor: colors.surface,
            borderTopColor: colors.line,
            height: TAB_BAR_HEIGHT + insets.bottom,
            paddingTop: 6,
            paddingBottom: insets.bottom + 6,
          },
          tabBarLabelStyle: { fontSize: 12, lineHeight: 16, fontWeight: "600" },
          sceneStyle: { backgroundColor: colors.paper, paddingBottom: uploadsShown ? UPLOADS_BAR_ROOM : 0 },
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
      <UploadsPanel bottom={TAB_BAR_HEIGHT + insets.bottom + stripHeight + space.sm} />
    </>
  );
}
