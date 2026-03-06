import NukeAnimator from './animators/nuke-animator.js';
import FirebombAnimator from './animators/firebomb-animator.js';
import FreezeAnimator from './animators/freeze-animator.js';
import SnipeAnimator from './animators/snipe-animator.js';

// 2. Map the string ID from your C# server to the JavaScript class
export const AnimatorRegistry = {
    'nuke': NukeAnimator,
    'firebomb': FirebombAnimator,
    'freeze': FreezeAnimator,
    'snipe': SnipeAnimator

    // (Future mappings go here)
    // 'divine_shield': DivineShieldAnimator
};