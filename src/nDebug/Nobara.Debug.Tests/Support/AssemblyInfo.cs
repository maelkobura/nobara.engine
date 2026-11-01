// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// Dear ImGui keeps its current context in a native global: two engines that
// draw it on two threads at once would share it, so the tests run one at a time.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
