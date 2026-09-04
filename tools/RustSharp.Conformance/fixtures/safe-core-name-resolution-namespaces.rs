pub mod api {
    pub mod model {
        pub type Shared = i32;
        pub const Shared: i32 = 7;
    }
}

use crate::api as ApiAlias;
use ApiAlias::model as ModelAlias;
use ModelAlias as PublicModel;

fn consume(value: PublicModel::Shared) -> PublicModel::Shared {
    PublicModel::Shared;
    return value;
}
