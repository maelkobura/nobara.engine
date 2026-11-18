// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

// OpenAL makes one context current for the whole process: the tests of a device run one after the other.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
