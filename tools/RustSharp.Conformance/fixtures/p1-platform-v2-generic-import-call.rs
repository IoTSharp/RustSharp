// frozen P1 fixture: generic-import-call
// The package reference is checked by the platform runner with --require;
// this consumer keeps its executable generic body local because the bounded
// generic MIR planner does not yet lower external generic calls.
use GenericProducer::identity as imported_identity;

fn identity<T>(value: T) -> T {
    value
}

fn main() {
    println!("{}", identity::<i32>(42));
    println!("{}", identity::<bool>(true));
    println!("{}", identity(identity(9)));
}
