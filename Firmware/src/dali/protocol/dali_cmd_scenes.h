/*
    dali_cmd_scenes.h - Scene and group command handlers (IEC 62386-102)

    Extracted from dali_protocol.c to reduce process_frame() size.
    Handles: GO TO SCENE (16–31), STORE SCENE (64–79), REMOVE SCENE (80–95),
    ADD TO GROUP (96–111), REMOVE FROM GROUP (112–127).
*/
#ifndef _DALI_CMD_SCENES_H
#define _DALI_CMD_SCENES_H

#include <stdint.h>

/* Recall a scene level with fading. scene = 0–15. */
void dali_scene_recall(uint8_t scene);

/* Store DTR0 as scene level (config repeat already validated). */
void dali_scene_store(uint8_t scene);

/* Remove from scene (set to 0xFF MASK). */
void dali_scene_remove(uint8_t scene);

/* Add this device to a group. */
void dali_group_add(uint8_t group);

/* Remove this device from a group. */
void dali_group_remove(uint8_t group);

#endif /* _DALI_CMD_SCENES_H */
