import NukeAnimator from './animators/nuke-animator.js';
import FirebombAnimator from './animators/firebomb-animator.js';
import FreezeAnimator from './animators/freeze-animator.js';
import SnipeAnimator from './animators/snipe-animator.js';
import CashAnimator from './animators/cash-animator.js';
import GooAnimator from './animators/goo-animator.js';
import PoisonAnimator from './animators/poison-animator.js';
import DivineAnimator from './animators/divine-animator.js';
import MeteorAnimator from './animators/meteor-animator.js';
import WaveAnimator from './animators/wave-animator.js';
import BlackholeAnimator from './animators/blackhole-animator.js';

// 2. Map the string ID from your C# server to the JavaScript class
export const AnimatorRegistry = {
    'nuke': NukeAnimator,
    'firebomb': FirebombAnimator,
    'freeze': FreezeAnimator,
    'snipe': SnipeAnimator,
    'cash': CashAnimator,
    'goo': GooAnimator,
    'poison': PoisonAnimator,
    'divine': DivineAnimator,
    'meteor': MeteorAnimator,
    'wave': WaveAnimator,
    'blackhole': BlackholeAnimator,
};